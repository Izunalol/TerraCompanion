using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace TerraCompanion.Hooks
{
    /// <summary>
    /// Entry points the TerraCompanion patcher injects into Terraria.exe:
    ///   Item.SetDefaults        start -> ItemEnter()   every return -> ItemExit(this)
    ///   NPC.SetDefaults         start -> NpcEnter()    every return -> NpcExit(this)
    ///   NPC.SpawnNPC            start -> Tick()
    ///   ContentSamples.Initialize start -> Startup()   end -> AfterSamples()
    /// Everything is reflection-based so one hooks DLL works across game versions, and every
    /// entry point swallows its own exceptions: a bad rule must never crash the game.
    /// </summary>
    public static class GameHooks
    {
        public const string Version = "5";

        // Terraria's true vanilla spawn settings. The multipliers in rules.txt are relative to these, NOT to whatever
        // the exe happens to contain (a hand-edited exe may already spawn several times faster).
        const int VanillaSpawnRate = 600, VanillaMaxSpawns = 5;

        static readonly object Sync = new object();
        static volatile RuleSet _rules = new RuleSet();
        static bool _started;
        static Assembly _game;
        static Type _itemType, _npcType;
        static string _dir, _rulesPath;
        static DateTime _rulesStamp;
        static int _lastCheck;
        static bool _suppress;
        static readonly Dictionary<string, object> StaticOriginals = new Dictionary<string, object>();
        static readonly HashSet<string> LoggedErrors = new HashSet<string>();

        [ThreadStatic] static int _itemDepth;
        [ThreadStatic] static int _npcDepth;

        // cached members
        static FieldInfo _itemTypeField, _itemMaxStack, _itemConsumable, _itemDamage, _itemAmmo, _itemAccessory, _itemPick, _itemAxe, _itemHammer, _itemNoMelee;
        static FieldInfo _npcTypeField, _npcNetId, _npcBoss, _npcFriendly, _npcTown, _npcLife, _npcLifeMax;

        // ------------------------------------------------------------------ entry points

        public static void Startup()
        {
            try { EnsureStarted(); }
            catch (Exception e) { Log("Startup failed: " + e); }
        }

        public static void AfterSamples()
        {
            try
            {
                EnsureStarted();
                DumpDefaults();
            }
            catch (Exception e) { Log("Dump failed: " + e); }
        }

        public static void Tick()
        {
            try
            {
                if (!_started) EnsureStarted();
                int now = Environment.TickCount;
                if (unchecked(now - _lastCheck) < 500) return;
                _lastCheck = now;
                if (ReloadIfChanged()) RefreshLocalPlayerItems();
            }
            catch (Exception e) { LogOnce("tick", "Tick failed: " + e); }
        }

        internal static Assembly GameAssembly { get { return _game; } }
        internal static RuleSet CurrentRules { get { return _rules; } }
        internal static string DataDir { get { return _dir; } }
        internal static void WriteLog(string message) { Log(message); }

        /// <summary>Called from Main.DrawInventory (Equipment page): draws and handles the extra pet slots.</summary>
        public static void DrawExtraSlots(int baseX, int baseY) { ExtraSlots.Draw(baseX, baseY); }

        /// <summary>Called from Player.Update right after the vanilla pet / light pet update.</summary>
        public static void UpdateExtraPets(object player, int index) { ExtraSlots.Update(player, index); }

        /// <summary>Player.ResetEffects stores the base minion count (vanilla 1) through this every frame.</summary>
        public static int MinionBase(int vanilla)
        {
            var rules = _rules; // volatile read; null-safe because it is initialised to an empty rule set
            return vanilla + rules.MinionBonus;
        }

        static FieldInfo _vanityPet, _lightPet, _buffTypeField;
        static MethodInfo _delBuff;

        /// <summary>
        /// Replaces Player.AddBuff_RemoveOldPetBuffsOfMatchingType. Returns true when it handled the removal
        /// (the patched method then returns immediately); false hands control back to vanilla.
        /// Vanilla deletes every existing pet (or light pet) buff when a new one arrives. With a limit above 1
        /// we only delete as many as needed to stay within the limit.
        /// </summary>
        public static bool HandlePetBuffs(object player, int type)
        {
            try
            {
                if (!_started) EnsureStarted();
                var rules = _rules;
                if (rules.MaxPets <= 1 && rules.MaxLightPets <= 1) return false; // nothing configured: pure vanilla behaviour
                if (_vanityPet == null)
                {
                    var main = _game.GetType("Terraria.Main");
                    var playerType = _game.GetType("Terraria.Player");
                    _vanityPet = main.GetField("vanityPet", BindingFlags.Static | BindingFlags.Public);
                    _lightPet = main.GetField("lightPet", BindingFlags.Static | BindingFlags.Public);
                    _buffTypeField = playerType.GetField("buffType", BindingFlags.Instance | BindingFlags.Public);
                    _delBuff = playerType.GetMethod("DelBuff", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(int) }, null);
                }
                var light = (bool[])_lightPet.GetValue(null);
                var pet = (bool[])_vanityPet.GetValue(null);
                if (type < 0 || type >= light.Length || type >= pet.Length) return false;
                var buffs = (int[])_buffTypeField.GetValue(player);
                if (light[type]) TrimBuffs(player, buffs, type, light, rules.MaxLightPets);
                if (pet[type]) TrimBuffs(player, buffs, type, pet, rules.MaxPets);
                return true;
            }
            catch (Exception e)
            {
                LogOnce("pets", "Pet limit failed, using vanilla behaviour: " + e.Message);
                return false;
            }
        }

        static void TrimBuffs(object player, int[] buffs, int newType, bool[] category, int limit)
        {
            limit = Math.Max(1, limit);
            // other pets of this kind that are already out (the same pet just gets its timer refreshed by AddBuff)
            var others = new List<int>();
            for (int i = 0; i < buffs.Length; i++)
            {
                int t = buffs[i];
                if (t > 0 && t != newType && t < category.Length && category[t]) others.Add(i);
            }
            int excess = others.Count - (limit - 1);
            if (excess <= 0) return;
            // Drop the pets in the lowest buff slots (the game keeps no age, so slot order is the best proxy for "oldest").
            // DelBuff shifts later buffs down, so delete the chosen slots from the highest index to the lowest.
            foreach (int slot in others.Take(excess).OrderByDescending(s => s))
                _delBuff.Invoke(player, new object[] { slot });
        }

        /// <summary>Main.Sundialing stores its cooldown (vanilla 8 days) through this.</summary>
        public static int SundialCooldown(int vanilla)
        {
            try
            {
                if (!_started) EnsureStarted();
                int days = _rules.SundialDays;
                return days >= 0 ? days : vanilla;
            }
            catch { return vanilla; }
        }

        /// <summary>Main.Moondialing stores its cooldown (vanilla 8 days) through this.</summary>
        public static int MoondialCooldown(int vanilla)
        {
            try
            {
                if (!_started) EnsureStarted();
                int days = _rules.MoondialDays;
                return days >= 0 ? days : vanilla;
            }
            catch { return vanilla; }
        }

        public static void ItemEnter()
        {
            if (_itemDepth > 64) _itemDepth = 0; // recover if an exception ever skipped an exit
            _itemDepth++;
        }

        public static void ItemExit(object item)
        {
            if (--_itemDepth > 0) return;
            _itemDepth = 0;
            var rules = _rules;
            if (_suppress || !rules.HasItemRules || item == null) return;
            try
            {
                int type = (int)_itemTypeField.GetValue(item);
                if (type <= 0) return;
                foreach (var r in rules.ItemSelectors)
                    if (ItemMatches(r.Selector, item)) Apply(r, item);
                List<FieldRule> list;
                if (rules.ItemById.TryGetValue(type, out list))
                    foreach (var r in list) Apply(r, item);
            }
            catch (Exception e) { LogOnce("item", "Item rule failed: " + e.Message); }
        }

        public static void NpcEnter()
        {
            if (_npcDepth > 64) _npcDepth = 0;
            _npcDepth++;
        }

        public static void NpcExit(object npc)
        {
            if (--_npcDepth > 0) return;
            _npcDepth = 0;
            var rules = _rules;
            if (_suppress || !rules.HasNpcRules || npc == null) return;
            try
            {
                int type = (int)_npcTypeField.GetValue(npc);
                if (type == 0) return;
                int netId = (int)_npcNetId.GetValue(npc);
                int lifeMaxBefore = (int)_npcLifeMax.GetValue(npc);
                foreach (var r in rules.NpcSelectors)
                    if (NpcMatches(r.Selector, npc)) Apply(r, npc);
                List<FieldRule> list;
                if (rules.NpcById.TryGetValue(netId, out list) || (netId != type && rules.NpcById.TryGetValue(type, out list)))
                    foreach (var r in list) Apply(r, npc);
                int lifeMax = (int)_npcLifeMax.GetValue(npc);
                if (lifeMax != lifeMaxBefore) _npcLife.SetValue(npc, lifeMax);
            }
            catch (Exception e) { LogOnce("npc", "NPC rule failed: " + e.Message); }
        }

        static void Apply(FieldRule r, object target)
        {
            try { r.Apply(target); }
            catch (Exception e) { LogOnce("apply:" + r.Field.Name, "Could not set " + r.Field.Name + ": " + e.Message); }
        }

        // ------------------------------------------------------------------ selectors

        static bool ItemMatches(string sel, object item)
        {
            switch (sel)
            {
                case "*": return true;
                case "stackable": return (int)_itemMaxStack.GetValue(item) > 1;
                case "consumable": return (bool)_itemConsumable.GetValue(item);
                case "ammo": return (int)_itemAmmo.GetValue(item) > 0;
                case "accessory": return (bool)_itemAccessory.GetValue(item);
                case "tool": return (int)_itemPick.GetValue(item) > 0 || (int)_itemAxe.GetValue(item) > 0 || (int)_itemHammer.GetValue(item) > 0;
                case "weapon":
                    return (int)_itemDamage.GetValue(item) > 0 && (int)_itemAmmo.GetValue(item) == 0 && !(bool)_itemAccessory.GetValue(item)
                           && !(bool)_itemConsumable.GetValue(item);
            }
            return false;
        }

        static bool NpcMatches(string sel, object npc)
        {
            switch (sel)
            {
                case "*": return true;
                case "boss": return (bool)_npcBoss.GetValue(npc);
                case "town": return (bool)_npcTown.GetValue(npc);
                case "friendly": return (bool)_npcFriendly.GetValue(npc);
                case "hostile": return !(bool)_npcFriendly.GetValue(npc) && !(bool)_npcTown.GetValue(npc) && (int)_npcLifeMax.GetValue(npc) > 5;
            }
            return false;
        }

        // ------------------------------------------------------------------ setup / reload

        static void EnsureStarted()
        {
            if (_started) return;
            lock (Sync)
            {
                if (_started) return;
                _game = FindGameAssembly();
                _itemType = _game.GetType("Terraria.Item", true);
                _npcType = _game.GetType("Terraria.NPC", true);
                const BindingFlags I = BindingFlags.Instance | BindingFlags.Public;
                _itemTypeField = _itemType.GetField("type", I);
                _itemMaxStack = _itemType.GetField("maxStack", I);
                _itemConsumable = _itemType.GetField("consumable", I);
                _itemDamage = _itemType.GetField("damage", I);
                _itemAmmo = _itemType.GetField("ammo", I);
                _itemAccessory = _itemType.GetField("accessory", I);
                _itemPick = _itemType.GetField("pick", I);
                _itemAxe = _itemType.GetField("axe", I);
                _itemHammer = _itemType.GetField("hammer", I);
                _itemNoMelee = _itemType.GetField("noMelee", I);
                _npcTypeField = _npcType.GetField("type", I);
                _npcNetId = _npcType.GetField("netID", I);
                _npcBoss = _npcType.GetField("boss", I);
                _npcFriendly = _npcType.GetField("friendly", I);
                _npcTown = _npcType.GetField("townNPC", I);
                _npcLife = _npcType.GetField("life", I);
                _npcLifeMax = _npcType.GetField("lifeMax", I);

                string savePath = null;
                try
                {
                    // Main.SavePath is a field in older builds and a property (-> Program.SavePath) in 1.4.5.
                    var main = _game.GetType("Terraria.Main");
                    const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                    var f = main.GetField("SavePath", S);
                    var p = main.GetProperty("SavePath", S);
                    savePath = (f != null ? f.GetValue(null) : p != null ? p.GetValue(null, null) : null) as string;
                }
                catch { }
                if (string.IsNullOrEmpty(savePath))
                    savePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Path.Combine("My Games", "Terraria"));
                _dir = Path.Combine(savePath, "TerraCompanion");
                Directory.CreateDirectory(_dir);
                _rulesPath = Path.Combine(_dir, "rules.txt");
                try { File.WriteAllText(Path.Combine(_dir, "hooks.log"), DateTime.Now + "  TerraCompanion hooks v" + Version + " loaded into " + _game.GetName().Name + " " + GameVersion() + Environment.NewLine); }
                catch { }
                _started = true;
                ReloadIfChanged(force: true);
            }
        }

        static Assembly FindGameAssembly()
        {
            var entry = Assembly.GetEntryAssembly();
            if (entry != null && entry.GetType("Terraria.Main", false) != null) return entry;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetType("Terraria.Main", false) != null) return a;
            throw new InvalidOperationException("Terraria assembly not found");
        }

        static string GameVersion()
        {
            try { return (string)_game.GetType("Terraria.Main").GetField("versionNumber", BindingFlags.Static | BindingFlags.Public).GetValue(null); }
            catch { return "?"; }
        }

        static bool ReloadIfChanged(bool force = false)
        {
            DateTime stamp = File.Exists(_rulesPath) ? File.GetLastWriteTimeUtc(_rulesPath) : DateTime.MinValue;
            if (!force && stamp == _rulesStamp) return false;
            _rulesStamp = stamp;
            string[] lines = stamp == DateTime.MinValue ? new string[0] : ReadAllLinesShared(_rulesPath);
            var rules = RuleSet.Parse(lines, _itemType, _npcType, _game);
            foreach (var err in rules.Errors) Log("rules.txt " + err);
            _rules = rules;
            ApplyStatics(rules);
            Log("Rules loaded: " + rules.ItemById.Count + " items, " + rules.ItemSelectors.Count + " item group rules, " + rules.NpcById.Count + " NPCs, "
                + rules.NpcSelectors.Count + " NPC group rules, spawnRate x" + rules.SpawnRate + ", maxSpawns x" + rules.MaxSpawns + ", " + rules.Statics.Count + " statics"
                + (rules.SundialDays >= 0 ? ", sundial " + rules.SundialDays + " days" : "")
                + (rules.MoondialDays >= 0 ? ", moondial " + rules.MoondialDays + " days" : "")
                + (rules.MinionBonus > 0 ? ", +" + rules.MinionBonus + " minions" : "")
                + (rules.MaxPets > 1 ? ", " + rules.MaxPets + " pets" : "")
                + (rules.MaxLightPets > 1 ? ", " + rules.MaxLightPets + " light pets" : ""));
            return true;
        }

        static string[] ReadAllLinesShared(string path)
        {
            // The companion may be writing the file at the same moment; retry briefly.
            for (int i = 0; ; i++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs, Encoding.UTF8))
                        return sr.ReadToEnd().Split('\n');
                }
                catch (IOException) { if (i > 5) throw; System.Threading.Thread.Sleep(20); }
            }
        }

        static void ApplyStatics(RuleSet rules)
        {
            var wanted = new Dictionary<string, KeyValuePair<FieldInfo, object>>();
            var npc = _npcType;
            var rate = npc.GetField("defaultSpawnRate", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var max = npc.GetField("defaultMaxSpawns", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (rate != null && max != null)
            {
                Original("Terraria.NPC.defaultSpawnRate", rate);
                int origRate = VanillaSpawnRate;
                Original("Terraria.NPC.defaultMaxSpawns", max);
                int origMax = VanillaMaxSpawns;
                wanted["Terraria.NPC.defaultSpawnRate"] = new KeyValuePair<FieldInfo, object>(rate, Math.Max(1, (int)Math.Round(origRate / rules.SpawnRate)));
                wanted["Terraria.NPC.defaultMaxSpawns"] = new KeyValuePair<FieldInfo, object>(max, Math.Max(0, (int)Math.Round(origMax * rules.MaxSpawns)));
            }
            foreach (var s in rules.Statics)
            {
                Original(s.Key, s.Field);
                wanted[s.Key] = new KeyValuePair<FieldInfo, object>(s.Field, s.Value);
            }
            // Restore anything that used to be overridden but no longer is.
            foreach (var kv in StaticOriginals.ToList())
                if (!wanted.ContainsKey(kv.Key))
                {
                    var f = FindStatic(kv.Key);
                    if (f != null) f.SetValue(null, kv.Value);
                }
            foreach (var kv in wanted)
            {
                try { kv.Value.Key.SetValue(null, kv.Value.Value); }
                catch (Exception e) { Log("Could not set " + kv.Key + ": " + e.Message); }
            }
        }

        static object Original(string key, FieldInfo f)
        {
            object v;
            if (!StaticOriginals.TryGetValue(key, out v)) StaticOriginals[key] = v = f.GetValue(null);
            return v;
        }

        static FieldInfo FindStatic(string key)
        {
            int dot = key.LastIndexOf('.');
            var t = _game.GetType(key.Substring(0, dot), false);
            return t == null ? null : t.GetField(key.Substring(dot + 1), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        }

        /// <summary>Re-runs SetDefaults (keeping prefix/stack/favourite) on the local player's items so edits show up instantly.</summary>
        static void RefreshLocalPlayerItems()
        {
            var main = _game.GetType("Terraria.Main");
            const BindingFlags S = BindingFlags.Static | BindingFlags.Public;
            if ((bool)main.GetField("dedServ", S).GetValue(null) || (bool)main.GetField("gameMenu", S).GetValue(null)) return;
            var players = (Array)main.GetField("player", S).GetValue(null);
            int me = (int)main.GetField("myPlayer", S).GetValue(null);
            object player = players.GetValue(me);
            if (player == null) return;
            var refresh = _itemType.GetMethod("Refresh", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(bool) }, null);
            if (refresh == null) return;
            var playerType = player.GetType();
            var arrays = new List<Array>();
            foreach (var name in new[] { "inventory", "armor", "dye", "miscEquips", "miscDyes" })
            {
                var f = playerType.GetField(name, BindingFlags.Instance | BindingFlags.Public);
                if (f != null) arrays.Add((Array)f.GetValue(player));
            }
            foreach (var name in new[] { "bank", "bank2", "bank3", "bank4" })
            {
                var f = playerType.GetField(name, BindingFlags.Instance | BindingFlags.Public);
                object chest = f == null ? null : f.GetValue(player);
                if (chest != null) arrays.Add((Array)chest.GetType().GetField("item").GetValue(chest));
            }
            int count = 0;
            foreach (var arr in arrays)
                foreach (var item in arr)
                    if (item != null && (int)_itemTypeField.GetValue(item) > 0) { refresh.Invoke(item, new object[] { false }); count++; }
            var mouse = main.GetField("mouseItem", S).GetValue(null);
            if (mouse != null && (int)_itemTypeField.GetValue(mouse) > 0) refresh.Invoke(mouse, new object[] { false });
            Log("Refreshed " + count + " items on the local player.");
        }

        // ------------------------------------------------------------------ vanilla data dump

        static readonly string[] ItemDumpFields =
        {
            "damage", "useTime", "useAnimation", "reuseDelay", "knockBack", "crit", "mana", "shootSpeed", "shoot", "useAmmo", "ammo", "notAmmo",
            "maxStack", "consumable", "autoReuse", "useTurn", "value", "rare", "defense", "pick", "axe", "hammer", "tileBoost", "healLife", "healMana",
            "potion", "buffType", "buffTime", "lifeRegen", "manaIncrease", "scale", "accessory", "vanity", "melee", "ranged", "magic", "summon",
            "noMelee", "channel", "createTile", "createWall", "useStyle", "fishingPole", "bait", "armorPenetration", "width", "height"
        };

        static readonly string[] NpcDumpFields =
        {
            "lifeMax", "damage", "defense", "knockBackResist", "value", "boss", "friendly", "townNPC", "scale", "npcSlots", "noGravity",
            "noTileCollide", "lavaImmune", "aiStyle", "width", "height", "rarity"
        };

        static void DumpDefaults()
        {
            string version = GameVersion();
            string itemsPath = Path.Combine(_dir, "items.tsv"), npcsPath = Path.Combine(_dir, "npcs.tsv");
            string marker = "#version\t" + version + "\t" + Version;
            if (File.Exists(itemsPath) && File.Exists(npcsPath) && FirstLine(itemsPath) == marker && FirstLine(npcsPath) == marker) return;

            _suppress = true;
            int itemErrors = 0, npcErrors = 0;
            try
            {
                var itemCount = Convert.ToInt32(_game.GetType("Terraria.ID.ItemID").GetField("Count").GetValue(null));
                var setDefaults = _itemType.GetMethods().First(m => m.Name == "SetDefaults" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(int));
                var nameProp = _itemType.GetProperty("Name");
                var sb = new StringBuilder();
                sb.Append(marker).Append('\n').Append("id\tname\t").Append(string.Join("\t", ItemDumpFields)).Append('\n');
                var fields = ItemDumpFields.Select(n => _itemType.GetField(n)).ToArray();
                for (int id = 1; id < itemCount; id++)
                {
                    try
                    {
                        var item = Activator.CreateInstance(_itemType);
                        setDefaults.Invoke(item, new object[] { id, null });
                        sb.Append(id).Append('\t').Append(Clean(SafeName(nameProp, item)));
                        foreach (var f in fields) sb.Append('\t').Append(f == null ? "" : ValueCodec.Format(f.GetValue(item)));
                        sb.Append('\n');
                    }
                    catch (Exception e) { itemErrors++; LogOnce("dumpitem", "Item dump error at " + id + ": " + Inner(e)); }
                }
                File.WriteAllText(itemsPath, sb.ToString());

                var npcId = _game.GetType("Terraria.ID.NPCID");
                int npcCount = Convert.ToInt32(npcId.GetField("Count").GetValue(null));
                int npcMin = Convert.ToInt32(npcId.GetField("NegativeIDCount").GetValue(null)) + 1;
                var npcSet = _npcType.GetMethods().First(m => m.Name == "SetDefaults" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(int));
                var spawnParams = Activator.CreateInstance(npcSet.GetParameters()[1].ParameterType);
                var typeName = _npcType.GetProperty("TypeName");
                var nfields = NpcDumpFields.Select(n => _npcType.GetField(n)).ToArray();
                sb.Length = 0;
                sb.Append(marker).Append('\n').Append("id\tname\t").Append(string.Join("\t", NpcDumpFields)).Append('\n');
                for (int id = npcMin; id < npcCount; id++)
                {
                    if (id == 0) continue;
                    try
                    {
                        var npc = Activator.CreateInstance(_npcType);
                        npcSet.Invoke(npc, new[] { (object)id, spawnParams });
                        sb.Append(id).Append('\t').Append(Clean(SafeName(typeName, npc)));
                        foreach (var f in nfields) sb.Append('\t').Append(f == null ? "" : ValueCodec.Format(f.GetValue(npc)));
                        sb.Append('\n');
                    }
                    catch (Exception e) { npcErrors++; LogOnce("dumpnpc", "NPC dump error at " + id + ": " + Inner(e)); }
                }
                File.WriteAllText(npcsPath, sb.ToString());
                Log("Wrote vanilla item/NPC stats for " + version + " (" + itemErrors + " item and " + npcErrors + " NPC entries skipped).");
            }
            finally { _suppress = false; }
        }

        static string SafeName(PropertyInfo p, object o)
        {
            try { return p == null ? "" : p.GetValue(o, null) as string; }
            catch { return ""; }
        }

        static string Inner(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e.GetType().Name + ": " + e.Message + " @ " + (e.StackTrace ?? "").Split('\n')[0].Trim();
        }

        static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

        static string FirstLine(string path)
        {
            try { using (var r = new StreamReader(path)) return r.ReadLine(); }
            catch { return null; }
        }

        // ------------------------------------------------------------------ logging

        static void LogOnce(string key, string msg)
        {
            lock (LoggedErrors) { if (!LoggedErrors.Add(key)) return; }
            Log(msg);
        }

        static void Log(string msg)
        {
            try
            {
                if (_dir == null) return;
                lock (Sync) File.AppendAllText(Path.Combine(_dir, "hooks.log"), DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }
    }
}
