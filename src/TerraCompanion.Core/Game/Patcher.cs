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

        // Extra pet / light pet equipment slots: (1) draw + handle them on the Equipment page, (2) apply their buffs each frame.
        var drawInventory = mod.GetType("Terraria.Main")?.Methods.FirstOrDefault(x => x.Name == "DrawInventory" && x.HasBody);
        log.Add(drawInventory != null && PatchEquipmentPage(drawInventory, Hook("DrawExtraSlots"))
            ? "Hooked Main.DrawInventory (extra pet slots on the Equipment page)."
            : "Equipment page layout not recognised: extra pet slots disabled.");
        var playerUpdate = player?.Methods.FirstOrDefault(x => x.Name == "Update" && !x.IsStatic && x.Parameters.Count == 1 && x.HasBody);
        log.Add(playerUpdate != null && PatchPetUpdate(playerUpdate, Hook("UpdateExtraPets"))
            ? "Hooked Player.Update (extra pet slots apply their pets)."
            : "Player.Update pet step not recognised: extra pet slots disabled.");

        // Dyes for the extra pet slots: Main.GetProjectileDesiredShader(Projectile) returns the shader id for a projectile;
        // let the hook override it for pets that sit in one of our slots.
        var shaderPicker = mod.GetType("Terraria.Main")?.Methods.FirstOrDefault(x => x.Name == "GetProjectileDesiredShader" && x.IsStatic && x.Parameters.Count == 1
                                                                                  && x.ReturnType.MetadataType == MetadataType.Int32 && x.HasBody);
        var shaderRets = shaderPicker?.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
        if (shaderPicker != null && shaderRets!.Count > 0)
        {
            var il = shaderPicker.Body.GetILProcessor();
            foreach (var ret in shaderRets)
            {
                var first = il.Create(OpCodes.Ldarg_0);
                il.InsertBefore(ret, first);
                il.InsertBefore(ret, il.Create(OpCodes.Call, Hook("PetShader")));
                RetargetBranches(shaderPicker.Body, ret, first);
            }
            log.Add("Hooked Main.GetProjectileDesiredShader (dyes for the extra pet slots).");
        }
        else log.Add("Projectile shader selection not recognised: dyes for the extra pet slots disabled.");

        // Extra accessory slots (with vanity + dye): seven insertions that only make sense together, so find every anchor
        // first and apply them all, or none.
        var accessoryPatches = new List<Action?>
        {
            drawInventory == null ? null : FindAccessoryPage(drawInventory, Hook("DrawExtraAccessories")),
            playerUpdateEquips(player) is { } ue ? FindAfterLoop(ue, "ApplyEquipFunctional", Hook("ExtraAccFunctional")) : null,
            playerUpdateEquips(player) is { } ue2 ? FindAfterLoop(ue2, "ApplyEquipVanity", Hook("ExtraAccVanity")) : null,
            player?.Methods.FirstOrDefault(x => x.Name == "UpdateDyes" && x.HasBody) is { } dyes ? FindDyes(dyes, Hook("ExtraAccDyesFunctional"), Hook("ExtraAccDyesVanity")) : null,
            player?.Methods.FirstOrDefault(x => x.Name == "PlayerFrame" && x.HasBody) is { } frame ? FindPlayerFrame(frame, Hook("ExtraAccVisualsFunctional"), Hook("ExtraAccVisualsVanity")) : null,
        };
        if (accessoryPatches.All(p => p != null))
        {
            foreach (var apply in accessoryPatches) apply!();
            log.Add("Hooked the accessory page, UpdateEquips, UpdateDyes and PlayerFrame (extra accessory slots with vanity and dyes).");
        }
        else log.Add("Accessory code not recognised (different game version?): extra accessory slots disabled. Missing: "
            + string.Join(", ", new[] { "page", "UpdateEquips(functional)", "UpdateEquips(vanity)", "UpdateDyes", "PlayerFrame" }.Where((_, i) => accessoryPatches[i] == null)));

        // Frame pacing / input latency: each insertion stands alone, so each one logs its own result.
        var doUpdate = main?.Methods.FirstOrDefault(x => x.Name == "DoUpdate" && x.HasBody);
        var focus = mod.GetType("Terraria.FocusHelper")?.Methods.FirstOrDefault(x => x.Name == "UpdateFocus" && x.HasBody);
        var cursorMethods = new[] { "DrawCursor", "DrawThickCursor" }
            .Select(n => main?.Methods.FirstOrDefault(x => x.Name == n && x.HasBody)).ToList();
        var paced = new[] { doUpdate, focus }.Concat(cursorMethods).Where(x => x != null).Select(x => x!.Body).ToList();
        foreach (var b in paced) b.SimplifyMacros(); // long branches while inserting; re-shortened below
        log.Add(doUpdate != null && PatchPartialFrame(doUpdate, Hook("PartialFrame"))
            ? "Hooked Main.DoUpdate partial frames (responsive mouse)."
            : "Main.DoUpdate partial-frame code not recognised: responsive mouse disabled.");
        log.Add(doUpdate != null && PatchFrameTiming(doUpdate, Hook("FrameTiming"))
            ? "Hooked Main.DoUpdate frame timing (VSync / FPS cap)."
            : "Main.DoUpdate frame-timing code not recognised: VSync / FPS cap options disabled.");
        // DrawCursor first resets the smart-interact state (keep that), then draws: skip only from the first instruction after the reset.
        var drawCursor = cursorMethods[0];
        var afterReset = drawCursor?.Body.Instructions.LastOrDefault(i => i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference f && f.Name.StartsWith("TileInteraction"))?.Next;
        if (focus != null && afterReset != null && cursorMethods[1] != null && PatchFocus(focus, Hook("BeforeFocus"), Hook("FilterMouseVisible")))
        {
            SkipWhen(drawCursor!, Hook("HideGameCursor"), afterReset);
            SkipWhen(cursorMethods[1]!, Hook("HideGameCursor"));
            log.Add("Hooked the cursor drawing and FocusHelper (Windows cursor option).");
        }
        else log.Add("Cursor code not recognised: Windows cursor option disabled.");
        foreach (var b in paced) b.OptimizeMacros();

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

    /// <summary>
    /// The Equipment page draws its slot grid in a loop and then moves on with
    ///   y += 247; x += 8;
    /// (to lay out the toggles below it). We insert a call right before that, passing the grid's origin
    /// (the same x / y locals the loop used), so the hook can draw extra slots to the left of the pet rows.
    /// Matched by shape, and only patched when it is found exactly once.
    /// </summary>
    static bool PatchEquipmentPage(MethodDefinition m, MethodReference hook)
    {
        var body = m.Body;
        body.SimplifyMacros();
        var matches = new List<(Instruction anchor, VariableDefinition x, VariableDefinition y)>();
        foreach (var i in body.Instructions)
        {
            // ldloc Y ; ldc.i4 247 ; add ; stloc Y ; ldloc X ; ldc.i4.8 ; add ; stloc X
            if (i.OpCode != OpCodes.Ldc_I4 || i.Operand is not int v || v != 247) continue;
            var load = i.Previous; var add = i.Next; var store = add?.Next;
            var loadX = store?.Next; var eight = loadX?.Next; var add2 = eight?.Next; var storeX = add2?.Next;
            if (load?.OpCode != OpCodes.Ldloc || add?.OpCode != OpCodes.Add || store?.OpCode != OpCodes.Stloc) continue;
            // (SimplifyMacros has turned ldc.i4.8 into a plain "ldc.i4 8")
            if (loadX?.OpCode != OpCodes.Ldloc || eight?.OpCode != OpCodes.Ldc_I4 || eight.Operand is not int e8 || e8 != 8
                || add2?.OpCode != OpCodes.Add || storeX?.OpCode != OpCodes.Stloc) continue;
            if (load.Operand != store.Operand || loadX.Operand != storeX.Operand) continue;
            matches.Add((load, (VariableDefinition)loadX.Operand, (VariableDefinition)load.Operand));
        }
        if (matches.Count != 1) { body.OptimizeMacros(); return false; }

        var (anchor, xVar, yVar) = matches[0];
        var il = body.GetILProcessor();
        var first = il.Create(OpCodes.Ldloc, xVar);
        il.InsertBefore(anchor, first);
        il.InsertBefore(anchor, il.Create(OpCodes.Ldloc, yVar));
        il.InsertBefore(anchor, il.Create(OpCodes.Call, hook));
        RetargetBranches(body, anchor, first);
        body.OptimizeMacros();
        return true;
    }

    static MethodDefinition? playerUpdateEquips(TypeDefinition? player) =>
        player?.Methods.FirstOrDefault(x => x.Name == "UpdateEquips" && !x.IsStatic && x.Parameters.Count == 1 && x.HasBody);

    /// <summary>
    /// The accessory page draws its slots in one loop (the only loop in DrawInventory that touches Player.armor,
    /// ItemSlot.Draw and IsItemSlotUnlockedAndUsable together). Right after that loop ends we call hook(baseY), where baseY is
    /// the page's top: the local the loop multiplies the row index by 56 with. Code placed there only runs when the loop falls
    /// through, i.e. only on this page.
    /// </summary>
    static Action? FindAccessoryPage(MethodDefinition m, MethodReference hook)
    {
        var body = m.Body;
        body.SimplifyMacros();
        var list = body.Instructions.ToList();
        var found = new List<(Instruction after, VariableDefinition baseY)>();
        for (int bi = 0; bi < list.Count; bi++)
        {
            var blt = list[bi];
            if (blt.OpCode != OpCodes.Blt || blt.Operand is not Instruction target) continue;
            int start = list.IndexOf(target);
            if (start < 0 || start >= bi) continue; // only backward branches (loops)
            var range = list.Skip(start).Take(bi - start + 1).ToList();
            bool Touches(string field) => range.Any(i => i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference f && f.Name == field && f.DeclaringType.Name == "Player");
            if (!Touches("armor") && !Touches("dye")) continue;
            if (!range.Any(i => i.Operand is MethodReference r && r.Name == "IsItemSlotUnlockedAndUsable")) continue;
            if (!range.Any(i => i.Operand is MethodReference r && r.DeclaringType.Name == "ItemSlot" && r.Name == "Draw")) continue;
            // ldloc <baseY> ; conv.r4 ; ldloc <row> ; ldc.i4 56 ; mul
            VariableDefinition? baseY = null;
            for (int k = 0; k + 4 < range.Count && baseY == null; k++)
                if (range[k].OpCode == OpCodes.Ldloc && range[k + 1].OpCode == OpCodes.Conv_R4 && range[k + 2].OpCode == OpCodes.Ldloc
                    && range[k + 3].OpCode == OpCodes.Ldc_I4 && range[k + 3].Operand is int row && row == 56 && range[k + 4].OpCode == OpCodes.Mul)
                    baseY = range[k].Operand as VariableDefinition;
            if (baseY != null) found.Add((blt, baseY));
        }
        // Three loops draw the page (functional slots, vanity slots, dyes); the extras go after the last one.
        if (found.Count != 3) { body.OptimizeMacros(); return null; }
        var (after, y) = found[2];
        return () =>
        {
            var il = body.GetILProcessor();
            var load = il.Create(OpCodes.Ldloc, y);
            il.InsertAfter(after, load);
            il.InsertAfter(load, il.Create(OpCodes.Call, hook));
            body.OptimizeMacros();
        };
    }

    /// <summary>Finds the loop that calls <paramref name="callName"/> and returns an insertion of hook(this) just after that loop ends.</summary>
    static Action? FindAfterLoop(MethodDefinition m, string callName, MethodReference hook)
    {
        var calls = m.Body.Instructions.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.Name == callName).ToList();
        if (calls.Count != 1) return null;
        Instruction? blt = calls[0];
        while (blt != null && blt.OpCode != OpCodes.Blt && blt.OpCode != OpCodes.Blt_S) blt = blt.Next;
        if (blt == null) return null;
        return () =>
        {
            var il = m.Body.GetILProcessor();
            var load = il.Create(OpCodes.Ldarg_0);
            il.InsertAfter(blt, load);
            il.InsertAfter(load, il.Create(OpCodes.Call, hook));
        };
    }

    /// <summary>
    /// UpdateDyes ends: ...cGrapple = miscDyes[4].dye; for (0..19) UpdateItemDye(...); cYorai = cPet.
    /// Extra functional dyes go right after the cGrapple store (before the loop), extra vanity dyes right before the cYorai assignment (after it).
    /// </summary>
    static Action? FindDyes(MethodDefinition m, MethodReference before, MethodReference after)
    {
        var ins = m.Body.Instructions;
        var grapple = ins.LastOrDefault(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Name == "cGrapple");
        var yorai = ins.LastOrDefault(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Name == "cYorai");
        // ldarg.0 ; ldarg.0 ; ldfld cPet ; stfld cYorai
        var start = yorai?.Previous?.Previous?.Previous;
        if (grapple == null || yorai == null || start?.OpCode != OpCodes.Ldarg_0 || yorai.Previous?.OpCode != OpCodes.Ldfld) return null;
        if (!ins.Any(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.Name == "UpdateItemDye")) return null;
        return () =>
        {
            var il = m.Body.GetILProcessor();
            var a = il.Create(OpCodes.Ldarg_0);
            il.InsertAfter(grapple, a);
            il.InsertAfter(a, il.Create(OpCodes.Call, before));
            il.InsertBefore(start, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(start, il.Create(OpCodes.Call, after));
        };
    }

    /// <summary>PlayerFrame: "if (!dead) UpdateVisibleAccessories();". Extra functional visuals go before the call, extra vanity visuals after it.</summary>
    static Action? FindPlayerFrame(MethodDefinition m, MethodReference before, MethodReference after)
    {
        var calls = m.Body.Instructions.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.Name == "UpdateVisibleAccessories").ToList();
        if (calls.Count != 1 || calls[0].Previous?.OpCode != OpCodes.Ldarg_0) return null;
        var call = calls[0]; var receiver = call.Previous!;
        return () =>
        {
            var il = m.Body.GetILProcessor();
            var first = il.Create(OpCodes.Ldarg_0);
            il.InsertBefore(receiver, first);
            il.InsertBefore(receiver, il.Create(OpCodes.Call, before));
            RetargetBranches(m.Body, receiver, first); // branches that reach the original call must run the extra-visuals hook too
            var load = il.Create(OpCodes.Ldarg_0);
            il.InsertAfter(call, load);
            il.InsertAfter(load, il.Create(OpCodes.Call, after));
        };
    }

    /// <summary>Player.Update calls UpdatePet(i) then UpdatePetLight(i). Right after the latter we call hook(this, i).</summary>
    static bool PatchPetUpdate(MethodDefinition m, MethodReference hook)
    {
        var call = m.Body.Instructions.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.Name == "UpdatePetLight").ToList();
        if (call.Count != 1) return false;
        var il = m.Body.GetILProcessor();
        var a = il.Create(OpCodes.Ldarg_0); var b = il.Create(OpCodes.Ldarg_1); var c = il.Create(OpCodes.Call, hook);
        il.InsertAfter(call[0], a); il.InsertAfter(a, b); il.InsertAfter(b, c);
        return true;
    }

    /// <summary>Points every branch that targeted <paramref name="from"/> at <paramref name="to"/> instead (code inserted before an anchor).</summary>
    /// <summary>
    /// With Frame Skip Off, DoUpdate returns early on frames that are drawn but not simulated:
    /// "if (accumulator &lt; frameTime) { if (...) SuppressDraw(); return; }". The only SuppressDraw directly followed by ret.
    /// hook() runs just before that ret.
    /// </summary>
    static bool PatchPartialFrame(MethodDefinition m, MethodReference hook)
    {
        var rets = m.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret && i.Previous is { OpCode.Code: Code.Callvirt } p
                                                  && p.Operand is MethodReference r && r.Name == "SuppressDraw").ToList();
        if (rets.Count != 1) return false;
        var il = m.Body.GetILProcessor();
        var call = il.Create(OpCodes.Call, hook);
        il.InsertBefore(rets[0], call);
        RetargetBranches(m.Body, rets[0], call); // the "no SuppressDraw" branch jumps straight to the ret
        return true;
    }

    /// <summary>DoUpdate sets IsFixedTimeStep / VSync for the frame-skip mode in an if/else; hook(this) runs where the branches meet.</summary>
    static bool PatchFrameTiming(MethodDefinition m, MethodReference hook)
    {
        var sets = m.Body.Instructions.Where(i => i.Operand is MethodReference r && r.Name == "set_SynchronizeWithVerticalRetrace").ToList();
        if (sets.Count != 2 || sets[1].Next == null) return false;
        var join = sets[1].Next;
        if (!m.Body.Instructions.Any(i => i.Operand == join)) return false; // must be the merge point of the if/else
        var il = m.Body.GetILProcessor();
        var load = il.Create(OpCodes.Ldarg_0);
        il.InsertBefore(join, load);
        il.InsertBefore(join, il.Create(OpCodes.Call, hook));
        RetargetBranches(m.Body, join, load);
        return true;
    }

    /// <summary>FocusHelper.UpdateFocus: before() at the start, and every IsMouseVisible = x becomes IsMouseVisible = filter(x).</summary>
    static bool PatchFocus(MethodDefinition m, MethodReference before, MethodReference filter)
    {
        var sets = m.Body.Instructions.Where(i => i.Operand is MethodReference r && r.Name == "set_IsMouseVisible").ToList();
        if (sets.Count == 0) return false;
        var il = m.Body.GetILProcessor();
        foreach (var s in sets) il.InsertBefore(s, il.Create(OpCodes.Call, filter));
        var first = m.Body.Instructions[0];
        var call = il.Create(OpCodes.Call, before);
        il.InsertBefore(first, call);
        RetargetBranches(m.Body, first, call);
        return true;
    }

    /// <summary>Inserts "if (hook()) return default;" at <paramref name="at"/> (default: the start) of a method returning void or a value type.</summary>
    static void SkipWhen(MethodDefinition m, MethodReference hook, Instruction? at = null)
    {
        var il = m.Body.GetILProcessor();
        var first = at ?? m.Body.Instructions[0];
        var head = new List<Instruction> { il.Create(OpCodes.Call, hook), il.Create(OpCodes.Brfalse, first) };
        if (m.ReturnType.MetadataType != MetadataType.Void)
        {
            var tmp = new VariableDefinition(m.ReturnType);
            m.Body.Variables.Add(tmp);
            m.Body.InitLocals = true;
            head.Add(il.Create(OpCodes.Ldloca, tmp));
            head.Add(il.Create(OpCodes.Initobj, m.ReturnType));
            head.Add(il.Create(OpCodes.Ldloc, tmp));
        }
        head.Add(il.Create(OpCodes.Ret));
        foreach (var i in head) il.InsertBefore(first, i);
        if (at != null) RetargetBranches(m.Body, first, head[0]); // jumps to the anchor must pass the check too (except our own brfalse)
        head[1].Operand = first;
    }

    static void RetargetBranches(MethodBody body, Instruction from, Instruction to)
    {
        foreach (var i in body.Instructions)
        {
            if (i.Operand == from && i != to) i.Operand = to;
            if (i.Operand is Instruction[] targets)
                for (int k = 0; k < targets.Length; k++) if (targets[k] == from) targets[k] = to;
        }
        foreach (var h in body.ExceptionHandlers)
        {
            if (h.TryStart == from) h.TryStart = to;
            if (h.TryEnd == from) h.TryEnd = to;
            if (h.HandlerStart == from) h.HandlerStart = to;
            if (h.HandlerEnd == from) h.HandlerEnd = to;
            if (h.FilterStart == from) h.FilterStart = to;
        }
    }

    static bool FilesEqual(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        return fa.Length == fb.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }
}
