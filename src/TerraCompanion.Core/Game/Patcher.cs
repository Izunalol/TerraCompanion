using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace TerraCompanion.Core.Game;

public enum PatchState { NotFound, Vanilla, Patched, PatchedOutdated }

public sealed record PatchStatus(PatchState State, string Detail, bool BackupExists);

/// <summary>
/// Injects calls to TerraCompanion.Hooks into Terraria.exe (or TerrariaServer.exe).
/// The untouched game is always kept as "&lt;exe&gt;.tcbak" and every patch starts from it,
/// so patching is idempotent and "Restore" gives back the exact original file.
/// </summary>
public static class Patcher
{
    public const string HooksAssemblyName = "TerraCompanion.Hooks";
    public const string HooksFileName = "TerraCompanion.Hooks.dll";
    const string HooksType = "TerraCompanion.Hooks.GameHooks";
    const string MarkerAttribute = "TerraCompanionPatched";

    public static string BackupPath(string exe) => exe + ".tcbak";

    public static PatchStatus GetStatus(string exe, string? bundledHooksPath = null)
    {
        if (!File.Exists(exe)) return new(PatchState.NotFound, "Game executable not found.", false);
        bool backup = File.Exists(BackupPath(exe));
        try
        {
            using var asm = AssemblyDefinition.ReadAssembly(exe, new ReaderParameters { ReadingMode = ReadingMode.Deferred });
            var marker = asm.MainModule.Types.FirstOrDefault(t => t.Name == MarkerAttribute);
            if (marker == null) return new(PatchState.Vanilla, "Not patched (vanilla game).", backup);
            string hooksInGame = Path.Combine(Path.GetDirectoryName(exe)!, HooksFileName);
            if (!File.Exists(hooksInGame)) return new(PatchState.PatchedOutdated, "Patched, but the hooks DLL is missing next to the game.", backup);
            if (bundledHooksPath != null && File.Exists(bundledHooksPath) && !FilesEqual(bundledHooksPath, hooksInGame))
                return new(PatchState.PatchedOutdated, "Patched with an older TerraCompanion build - re-patch to update.", backup);
            return new(PatchState.Patched, "Patched - rules are active.", backup);
        }
        catch (Exception e)
        {
            return new(PatchState.NotFound, "Could not read the game executable: " + e.Message, backup);
        }
    }

    /// <summary>Patches the game. Returns a short human-readable log.</summary>
    public static string Patch(string exe, string hooksDllPath)
    {
        var log = new List<string>();
        string dir = Path.GetDirectoryName(exe)!;
        string backup = BackupPath(exe);

        // 1. Make sure we have a clean original. If the current exe is vanilla (fresh install or a
        //    Steam update replaced it), it becomes the new backup.
        bool currentIsVanilla;
        using (var cur = AssemblyDefinition.ReadAssembly(exe, new ReaderParameters { ReadingMode = ReadingMode.Deferred }))
            currentIsVanilla = cur.MainModule.Types.All(t => t.Name != MarkerAttribute);
        if (currentIsVanilla)
        {
            File.Copy(exe, backup, overwrite: true);
            log.Add($"Backed up the original game to {Path.GetFileName(backup)}.");
        }
        else if (!File.Exists(backup))
            throw new InvalidOperationException("The game is already patched but the original backup is missing. Use Steam's 'Verify integrity of game files' to restore it, then patch again.");

        // 2. Copy the hooks next to the game so the runtime finds it.
        File.Copy(hooksDllPath, Path.Combine(dir, HooksFileName), overwrite: true);
        log.Add($"Installed {HooksFileName}.");

        // 3. Patch from the clean backup into memory, then replace the exe.
        using var resolver = new GameAssemblyResolver(backup);
        using var hooks = AssemblyDefinition.ReadAssembly(hooksDllPath);
        var hookType = hooks.MainModule.GetType(HooksType) ?? throw new InvalidOperationException("Hooks DLL is invalid.");
        using var game = AssemblyDefinition.ReadAssembly(backup, new ReaderParameters { ReadingMode = ReadingMode.Immediate, AssemblyResolver = resolver });
        var mod = game.MainModule;

        MethodReference Hook(string name) => mod.ImportReference(hookType.Methods.Single(m => m.Name == name));

        var item = mod.GetType("Terraria.Item") ?? throw new InvalidOperationException("Terraria.Item not found - unsupported game version.");
        var npc = mod.GetType("Terraria.NPC") ?? throw new InvalidOperationException("Terraria.NPC not found - unsupported game version.");

        var itemSetDefaults = MainSetDefaults(item);
        Wrap(itemSetDefaults, Hook("ItemEnter"), Hook("ItemExit"));
        log.Add($"Hooked {itemSetDefaults.FullName}.");

        var npcSetDefaults = MainSetDefaults(npc);
        Wrap(npcSetDefaults, Hook("NpcEnter"), Hook("NpcExit"));
        log.Add($"Hooked {npcSetDefaults.FullName}.");

        var spawn = npc.Methods.FirstOrDefault(m => m.Name == "SpawnNPC" && m.IsStatic && m.Parameters.Count == 0 && m.HasBody)
                    ?? throw new InvalidOperationException("NPC.SpawnNPC not found - unsupported game version.");
        Wrap(spawn, Hook("Tick"), null);
        log.Add("Hooked NPC.SpawnNPC (live rule reload + spawn rates).");

        var samples = mod.GetType("Terraria.ID.ContentSamples")?.Methods.FirstOrDefault(m => m.Name == "Initialize" && m.IsStatic && m.HasBody);
        if (samples != null)
        {
            Wrap(samples, Hook("Startup"), Hook("AfterSamples"), passThis: false);
            log.Add("Hooked ContentSamples.Initialize (startup + vanilla stat export).");
        }
        else log.Add("ContentSamples not found: vanilla stat export disabled.");

        var main = mod.GetType("Terraria.Main");
        foreach (var (method, field, hook) in new[] { ("Sundialing", "sundialCooldown", "SundialCooldown"), ("Moondialing", "moondialCooldown", "MoondialCooldown") })
        {
            var m = main?.Methods.FirstOrDefault(x => x.Name == method && x.HasBody);
            int n = m == null ? 0 : FilterStore(m, field, Hook(hook));
            log.Add(n > 0 ? $"Hooked Main.{method} (custom cooldown)." : $"Main.{method} not found: {method} cooldown setting disabled.");
        }

        // Minion slots: the base value (vanilla 1) is stored by Player.ResetEffects every frame; add our bonus there.
        var player = mod.GetType("Terraria.Player");
        var reset = player?.Methods.FirstOrDefault(x => x.Name == "ResetEffects" && !x.IsStatic && x.Parameters.Count == 0 && x.HasBody);
        int minionStores = reset == null ? 0 : FilterStore(reset, "maxMinions", Hook("MinionBase"));
        log.Add(minionStores == 1 ? "Hooked Player.ResetEffects (extra minion slots)." : "Player.ResetEffects not as expected: extra minion slots disabled.");
        if (minionStores > 1) throw new InvalidOperationException("Unexpected number of maxMinions stores in ResetEffects.");

        // Pets / light pets: vanilla deletes the old pet buff whenever a new one is added. Let the hook decide first.
        var petRemoval = player?.Methods.FirstOrDefault(x => x.Name == "AddBuff_RemoveOldPetBuffsOfMatchingType" && !x.IsStatic && x.Parameters.Count == 1 && x.HasBody);
        if (petRemoval != null)
        {
            var il = petRemoval.Body.GetILProcessor();
            var first = petRemoval.Body.Instructions[0];
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
            il.InsertBefore(first, il.Create(OpCodes.Call, Hook("HandlePetBuffs")));
            il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
            il.InsertBefore(first, il.Create(OpCodes.Ret));
            log.Add("Hooked Player pet buff handling (pet / light pet limits).");
        }
        else log.Add("Pet buff handling not found: pet limits disabled.");

        // Marker type so we can recognise a patched exe.
        mod.Types.Add(new TypeDefinition("", MarkerAttribute, TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.Abstract, mod.TypeSystem.Object));

        string tmp = exe + ".tcnew";
        game.Write(tmp);
        File.Move(tmp, exe, overwrite: true);
        log.Add($"Wrote patched {Path.GetFileName(exe)}.");
        return string.Join(Environment.NewLine, log);
    }

    public static string Restore(string exe)
    {
        string backup = BackupPath(exe);
        if (!File.Exists(backup)) throw new InvalidOperationException("No backup found. Use Steam's 'Verify integrity of game files' to restore the original.");
        File.Copy(backup, exe, overwrite: true);
        return $"Restored the original {Path.GetFileName(exe)}.";
    }

    /// <summary>The SetDefaults overload that does the real work: (int, ...) with the most parameters.</summary>
    static MethodDefinition MainSetDefaults(TypeDefinition t) =>
        t.Methods.Where(m => m.Name == "SetDefaults" && !m.IsStatic && m.HasBody && m.Parameters.Count >= 1 && m.Parameters[0].ParameterType.MetadataType == MetadataType.Int32
                             && m.ReturnType.MetadataType == MetadataType.Void)
                 .OrderByDescending(m => m.Parameters.Count).ThenByDescending(m => m.Body.CodeSize)
                 .FirstOrDefault() ?? throw new InvalidOperationException($"{t.Name}.SetDefaults not found - unsupported game version.");

    /// <summary>
    /// Calls <paramref name="enter"/> first thing and <paramref name="exit"/>(this) before every return.
    /// Each existing 'ret' is rewritten in place (so branch targets stay valid) into the exit call.
    /// </summary>
    static void Wrap(MethodDefinition m, MethodReference? enter, MethodReference? exit, bool passThis = true)
    {
        if (m.ReturnType.MetadataType != MetadataType.Void) throw new InvalidOperationException($"{m.Name} does not return void.");
        var body = m.Body;
        body.SimplifyMacros();
        var il = body.GetILProcessor();
        if (exit != null)
        {
            foreach (var ret in body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
            {
                if (passThis)
                {
                    ret.OpCode = OpCodes.Ldarg_0; ret.Operand = null;
                    var call = il.Create(OpCodes.Call, exit);
                    il.InsertAfter(ret, call);
                    il.InsertAfter(call, il.Create(OpCodes.Ret));
                }
                else
                {
                    ret.OpCode = OpCodes.Call; ret.Operand = exit;
                    il.InsertAfter(ret, il.Create(OpCodes.Ret));
                }
            }
        }
        if (enter != null) il.InsertBefore(body.Instructions[0], il.Create(OpCodes.Call, enter));
        body.OptimizeMacros();
    }

    /// <summary>
    /// Passes every value stored into static int field <paramref name="field"/> inside <paramref name="m"/>
    /// through <paramref name="filter"/> (int -> int), e.g. "ldc.i4.8; stsfld sundialCooldown" becomes
    /// "ldc.i4.8; call SundialCooldown(int); stsfld sundialCooldown". Returns the number of stores hooked.
    /// </summary>
    static int FilterStore(MethodDefinition m, string field, MethodReference filter)
    {
        var il = m.Body.GetILProcessor();
        // stsfld (static) or stfld (instance): in both cases the value to store is on top of the stack when we insert the call
        var stores = m.Body.Instructions.Where(i => (i.OpCode == OpCodes.Stsfld || i.OpCode == OpCodes.Stfld) && i.Operand is FieldReference f && f.Name == field
                                                    && f.FieldType.MetadataType == MetadataType.Int32).ToList();
        foreach (var store in stores)
        {
            // Insert after the value is pushed: retarget nothing, the call slots in right before the store.
            var call = il.Create(OpCodes.Call, filter);
            il.InsertBefore(store, call);
            foreach (var i in m.Body.Instructions)
            {
                if (i.Operand == store && i != call) i.Operand = call;
                if (i.Operand is Instruction[] targets)
                    for (int k = 0; k < targets.Length; k++) if (targets[k] == store) targets[k] = call;
            }
        }
        return stores.Count;
    }

    static bool FilesEqual(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        return fa.Length == fb.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }
}
