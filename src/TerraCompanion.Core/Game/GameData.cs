using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using TerraCompanion.Core.Assets;

namespace TerraCompanion.Core.Game;

/// <summary>
/// Everything the companion needs to know about the installed game, read straight out of
/// Terraria.exe (IDs, names, counts, map colors) and its Content folder (item icons).
/// Nothing here is hard-coded per game version.
/// </summary>
public sealed class GameData
{
    public string ExePath = "";
    public string ContentDir = "";
    public string Version = "?";
    public int Release;

    public int ItemCount, NpcCount, TileCount, WallCount, BuffCount, PrefixCount;
    public string[] ItemNames = Array.Empty<string>();
    public string[] ItemInternalNames = Array.Empty<string>();
    public string[] TileNames = Array.Empty<string>();
    public string[] WallNames = Array.Empty<string>();
    public string[] BuffNames = Array.Empty<string>();
    public string[] PrefixNames = Array.Empty<string>();
    public SortedDictionary<int, string> NpcNames = new();
    public SortedDictionary<int, string> NpcInternalNames = new();
    public Dictionary<int, int> ItemAnimationFrames = new();
    /// <summary>Vanilla values of a few static fields the World Rules page exposes, e.g. "Terraria.Item.potionDelay".</summary>
    public Dictionary<string, int> StaticDefaults = new();
    /// <summary>Buff ids that are pets / light pets (read from Main.vanityPet / Main.lightPet).</summary>
    public HashSet<int> PetBuffs = new(), LightPetBuffs = new();
    public MapColors Map = new();

    readonly ConcurrentDictionary<int, TextureData?> _icons = new();

    public string ItemName(int id) => id > 0 && id < ItemNames.Length ? ItemNames[id] : id == 0 ? "" : $"Unknown item {id}";
    public string NpcName(int id) => NpcNames.TryGetValue(id, out var n) ? n : $"NPC {id}";
    public string PrefixName(int id) => id > 0 && id < PrefixNames.Length ? PrefixNames[id] : "";
    public string BuffName(int id) => id > 0 && id < BuffNames.Length ? BuffNames[id] : id == 0 ? "" : $"Buff {id}";
    public string TileName(int id) => id >= 0 && id < TileNames.Length ? TileNames[id] : $"Tile {id}";
    public string WallName(int id) => id > 0 && id < WallNames.Length ? WallNames[id] : $"Wall {id}";

    public static GameData Load(string exePath)
    {
        var g = new GameData { ExePath = exePath, ContentDir = Path.Combine(Path.GetDirectoryName(exePath)!, "Content") };
        using var asm = AssemblyDefinition.ReadAssembly(exePath, new ReaderParameters { ReadingMode = ReadingMode.Deferred });
        var mod = asm.MainModule;

        var main = mod.GetType("Terraria.Main");
        g.Version = Const(main, "assemblyVersionNumber") as string ?? asm.Name.Version.ToString();
        g.Release = Const(main, "curRelease") is int rel ? rel : 0;

        g.ItemCount = CountFromCctor(mod, "Terraria.ID.ItemID");
        g.NpcCount = CountFromCctor(mod, "Terraria.ID.NPCID");
        g.TileCount = CountFromCctor(mod, "Terraria.ID.TileID");
        g.WallCount = CountFromCctor(mod, "Terraria.ID.WallID");
        g.BuffCount = CountFromCctor(mod, "Terraria.ID.BuffID");
        g.PrefixCount = CountFromCctor(mod, "Terraria.ID.PrefixID");

        var loc = LoadLocalization(mod);
        string L(string key, string fallback) => loc.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;

        var itemIds = Constants(mod, "Terraria.ID.ItemID");
        if (g.ItemCount == 0) g.ItemCount = itemIds.Keys.DefaultIfEmpty(0).Max() + 1;
        g.ItemNames = new string[g.ItemCount];
        g.ItemInternalNames = new string[g.ItemCount];
        foreach (var (id, name) in itemIds)
            if (id > 0 && id < g.ItemCount) { g.ItemInternalNames[id] = name; g.ItemNames[id] = L("ItemName." + name, Humanize(name)); }

        foreach (var (id, name) in Constants(mod, "Terraria.ID.NPCID"))
        {
            if (name is "NegativeIDCount") continue;
            g.NpcInternalNames[id] = name;
            g.NpcNames[id] = L("NPCName." + name, Humanize(name));
        }

        g.BuffNames = Named(Constants(mod, "Terraria.ID.BuffID"), g.BuffCount, n => L("BuffName." + n, Humanize(n)));
        g.PrefixNames = Named(Constants(mod, "Terraria.ID.PrefixID"), g.PrefixCount, n => L("Prefix." + n, Humanize(n)));
        g.TileNames = Named(Constants(mod, "Terraria.ID.TileID"), g.TileCount, Humanize);
        g.WallNames = Named(Constants(mod, "Terraria.ID.WallID"), g.WallCount, Humanize);

        foreach (var key in new[] { "Terraria.Item.potionDelay", "Terraria.Item.restorationDelay", "Terraria.Item.mushroomDelay", "Terraria.Item.eggnogDelay",
                                    "Terraria.NPC.defaultSpawnRate", "Terraria.NPC.defaultMaxSpawns" })
            if (StaticInitInt(mod, key) is int v) g.StaticDefaults[key] = v;
        // Sundial / Moondial cooldowns are constants inside the methods that use them (days).
        foreach (var (method, field) in new[] { ("Sundialing", "sundialCooldown"), ("Moondialing", "moondialCooldown") })
        {
            var m = main?.Methods.FirstOrDefault(x => x.Name == method && x.HasBody);
            foreach (var ins in m?.Body.Instructions ?? Enumerable.Empty<Instruction>())
            {
                if (ins.OpCode != OpCodes.Stsfld || ins.Operand is not FieldReference fr || fr.Name != field) continue;
                // In a patched exe our filter call sits between the constant and the store.
                var src = ins.Previous?.OpCode == OpCodes.Call ? ins.Previous.Previous : ins.Previous;
                if (LoadedInt(src) is int days) g.StaticDefaults["Terraria.Main." + method] = days;
            }
        }

        g.PetBuffs = ReadBuffFlags(main, "vanityPet");
        g.LightPetBuffs = ReadBuffFlags(main, "lightPet");

        g.ItemAnimationFrames = ReadItemAnimations(main);
        g.Map = MapColorExtractor.Extract(mod, Math.Max(g.TileCount, 1), Math.Max(g.WallCount, 1));
        return g;
    }

    // ------------------------------------------------------------ icons

    /// <summary>Item icon (first animation frame), or null if missing. Thread-safe and cached.</summary>
    public TextureData? GetItemIcon(int id)
    {
        if (id <= 0) return null;
        return _icons.GetOrAdd(id, i =>
        {
            try
            {
                string f = Path.Combine(ContentDir, "Images", $"Item_{i}.xnb");
                if (!File.Exists(f)) return null;
                var t = XnbTexture.Load(f);
                if (ItemAnimationFrames.TryGetValue(i, out int frames) && frames > 1)
                    t = t.Crop(0, 0, t.Width, t.Height / frames);
                return t;
            }
            catch { return null; }
        });
    }

    // ------------------------------------------------------------ IL helpers

    static object? Const(TypeDefinition? t, string name) => t?.Fields.FirstOrDefault(f => f.Name == name && f.HasConstant)?.Constant;

    static Dictionary<int, string> Constants(ModuleDefinition mod, string typeName)
    {
        var d = new Dictionary<int, string>();
        var t = mod.GetType(typeName);
        if (t == null) return d;
        foreach (var f in t.Fields)
        {
            if (!f.IsStatic || !f.IsLiteral || !f.HasConstant) continue;
            int id = f.Constant switch { short s => s, ushort u => u, int i => i, byte b => b, _ => int.MinValue };
            if (id == int.MinValue) continue;
            d.TryAdd(id, f.Name);
        }
        return d;
    }

    /// <summary>Initial value of a static int field, as assigned in its type's static constructor ("Namespace.Type.field").</summary>
    static int? StaticInitInt(ModuleDefinition mod, string key)
    {
        int dot = key.LastIndexOf('.');
        var cctor = mod.GetType(key[..dot])?.Methods.FirstOrDefault(m => m.IsConstructor && m.IsStatic && m.HasBody);
        string field = key[(dot + 1)..];
        foreach (var ins in cctor?.Body.Instructions ?? Enumerable.Empty<Instruction>())
            if (ins.OpCode == OpCodes.Stsfld && ins.Operand is FieldReference fr && fr.Name == field && LoadedInt(ins.Previous) is int n)
                return n;
        return null;
    }

    static int CountFromCctor(ModuleDefinition mod, string typeName)
    {
        var t = mod.GetType(typeName);
        if (t == null) return 0;
        var lit = t.Fields.FirstOrDefault(f => f.Name == "Count" && f.HasConstant);
        if (lit != null) return Convert.ToInt32(lit.Constant);
        var cctor = t.Methods.FirstOrDefault(m => m.IsConstructor && m.IsStatic && m.HasBody);
        if (cctor == null) return 0;
        foreach (var ins in cctor.Body.Instructions)
            if (ins.OpCode == OpCodes.Stsfld && ins.Operand is FieldReference fr && fr.Name == "Count" && LoadedInt(ins.Previous) is int n)
                return n;
        return 0;
    }

    static int? LoadedInt(Instruction? i)
    {
        if (i == null) return null;
        var c = i.OpCode.Code;
        if (c >= Code.Ldc_I4_M1 && c <= Code.Ldc_I4_8) return c == Code.Ldc_I4_M1 ? -1 : c - Code.Ldc_I4_0;
        if (c == Code.Ldc_I4_S) return (sbyte)i.Operand;
        if (c == Code.Ldc_I4) return (int)i.Operand;
        return null;
    }

    /// <summary>
    /// Buff ids the game flags in a static bool array such as Main.vanityPet / Main.lightPet. The game sets them with
    /// "ldsfld array; ldc id; ldc.i4.1; stelem.i1" inside its static setup methods.
    /// </summary>
    static HashSet<int> ReadBuffFlags(TypeDefinition? main, string arrayName)
    {
        var ids = new HashSet<int>();
        foreach (var m in main?.Methods.Where(x => x.HasBody && (x.Name == "Initialize_TileAndNPCData1" || x.IsConstructor && x.IsStatic)) ?? Enumerable.Empty<MethodDefinition>())
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.OpCode != OpCodes.Ldsfld || ins.Operand is not FieldReference fr || fr.Name != arrayName) continue;
                var id = ins.Next; var one = id?.Next; var store = one?.Next;
                if (store?.OpCode == OpCodes.Stelem_I1 && LoadedInt(one) == 1 && LoadedInt(id) is int buff) ids.Add(buff);
            }
        return ids;
    }

    static Dictionary<int, int> ReadItemAnimations(TypeDefinition? main)
    {
        var d = new Dictionary<int, int>();
        var m = main?.Methods.FirstOrDefault(x => x.Name == "InitializeItemAnimations" && x.HasBody);
        if (m == null) return d;
        foreach (var ins in m.Body.Instructions)
        {
            // ldc type ; ldc ticks ; ldc frames ; ldc pingpong ; newobj DrawAnimationVertical(int,int,bool)
            if (ins.OpCode != OpCodes.Newobj || ins.Operand is not MethodReference ctor || ctor.DeclaringType.Name != "DrawAnimationVertical") continue;
            var frames = ctor.Parameters.Count == 3 ? ins.Previous?.Previous : ctor.Parameters.Count == 2 ? ins.Previous : null;
            var type = ctor.Parameters.Count == 3 ? frames?.Previous?.Previous : frames?.Previous?.Previous;
            if (LoadedInt(frames) is int f && LoadedInt(type) is int id) d[id] = f;
        }
        return d;
    }

    // ------------------------------------------------------------ localization

    static readonly Regex RefPattern = new(@"\{\$([A-Za-z0-9_.]+)\}", RegexOptions.Compiled);

    static Dictionary<string, string> LoadLocalization(ModuleDefinition mod)
    {
        var flat = new Dictionary<string, string>(StringComparer.Ordinal);
        var opts = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
        foreach (var res in mod.Resources.OfType<EmbeddedResource>())
        {
            if (!res.Name.StartsWith("Terraria.Localization.Content.en-US", StringComparison.Ordinal) || !res.Name.EndsWith(".json")) continue;
            try
            {
                using var doc = JsonDocument.Parse(res.GetResourceData(), opts);
                foreach (var section in doc.RootElement.EnumerateObject())
                {
                    if (section.Value.ValueKind != JsonValueKind.Object) continue;
                    foreach (var kv in section.Value.EnumerateObject())
                        if (kv.Value.ValueKind == JsonValueKind.String)
                            flat[section.Name + "." + kv.Name] = kv.Value.GetString() ?? "";
                }
            }
            catch { /* a broken file only costs us nicer names */ }
        }
        foreach (var key in flat.Keys.ToList())
            flat[key] = Resolve(flat, flat[key], 0);
        return flat;
    }

    static string Resolve(Dictionary<string, string> flat, string s, int depth)
    {
        if (depth > 5 || !s.Contains("{$")) return s;
        return RefPattern.Replace(s, m => flat.TryGetValue(m.Groups[1].Value, out var r) ? Resolve(flat, r, depth + 1) : m.Value);
    }

    static string[] Named(Dictionary<int, string> ids, int count, Func<string, string> name)
    {
        if (count <= 0) count = ids.Keys.DefaultIfEmpty(0).Max() + 1;
        var a = new string[count];
        foreach (var (id, n) in ids)
            if (id >= 0 && id < count) a[id] = name(n);
        for (int i = 0; i < count; i++) a[i] ??= $"#{i}";
        return a;
    }

    public static string Humanize(string s) => Regex.Replace(s, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
}
