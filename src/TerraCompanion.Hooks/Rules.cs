using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace TerraCompanion.Hooks
{
    /// <summary>
    /// The rules file is plain text, one rule per line (see README / the companion's "Raw rules" tab):
    ///   spawnRate = 2                       spawn-rate multiplier (2 = twice as many spawns)
    ///   maxSpawns = 1.5                     max-enemies multiplier
    ///   static Terraria.Item.potionDelay = 1800
    ///   item 757 damage = 300               item &lt;id|*|stackable|consumable|weapon|ammo|accessory|tool&gt; &lt;field&gt; &lt;=|*=|+=&gt; &lt;value&gt;
    ///   npc boss lifeMax *= 2               npc &lt;id|*|boss|hostile|friendly|town&gt; ...
    /// Selector rules apply first, then rules for a specific id, so a specific id always wins.
    /// </summary>
    sealed class RuleSet
    {
        public double SpawnRate = 1, MaxSpawns = 1;
        /// <summary>Days of Enchanted Sundial / Moondial cooldown; -1 = vanilla.</summary>
        public int SundialDays = -1, MoondialDays = -1;
        /// <summary>Extra minion slots on top of vanilla's base of 1 (accessories and armor still add theirs).</summary>
        public int MinionBonus;
        /// <summary>How many pets / light pets may be out at once (vanilla 1 each).</summary>
        public int MaxPets = 1, MaxLightPets = 1;
        /// <summary>Show extra equipment slots (Equipment tab) for the extra pets / light pets. On by default; "petSlots = 0" turns it off.</summary>
        public bool PetSlots = true;
        /// <summary>Extra accessory slots (each with its own vanity and dye slot) on the accessory page; 0 = vanilla.</summary>
        public int ExtraAccessories;
        public readonly List<StaticRule> Statics = new List<StaticRule>();
        public readonly List<FieldRule> ItemSelectors = new List<FieldRule>();
        public readonly Dictionary<int, List<FieldRule>> ItemById = new Dictionary<int, List<FieldRule>>();
        public readonly List<FieldRule> NpcSelectors = new List<FieldRule>();
        public readonly Dictionary<int, List<FieldRule>> NpcById = new Dictionary<int, List<FieldRule>>();
        public readonly List<string> Errors = new List<string>();

        public bool HasItemRules => ItemSelectors.Count > 0 || ItemById.Count > 0;
        public bool HasNpcRules => NpcSelectors.Count > 0 || NpcById.Count > 0;

        public static RuleSet Parse(string[] lines, Type itemType, Type npcType, Assembly game)
        {
            var rs = new RuleSet();
            for (int n = 0; n < lines.Length; n++)
            {
                string line = lines[n];
                int hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash);
                line = line.Trim();
                if (line.Length == 0) continue;
                try { rs.ParseLine(line, itemType, npcType, game); }
                catch (Exception e) { rs.Errors.Add("line " + (n + 1) + ": " + e.Message); }
            }
            return rs;
        }

        void ParseLine(string line, Type itemType, Type npcType, Assembly game)
        {
            string[] t = Tokenize(line);
            string head = t[0].ToLowerInvariant();
            if ((head == "spawnrate" || head == "maxspawns") && t.Length == 3 && t[1] == "=")
            {
                double v = double.Parse(t[2], CultureInfo.InvariantCulture);
                if (v <= 0 || v > 1000) throw new Exception("multiplier must be between 0 and 1000");
                if (head == "spawnrate") SpawnRate = v; else MaxSpawns = v;
                return;
            }
            if (head == "extraaccessories" && t.Length == 3 && t[1] == "=")
            {
                int n = int.Parse(t[2], CultureInfo.InvariantCulture);
                if (n < 0 || n > 14) throw new Exception("extraAccessories must be between 0 and 14");
                ExtraAccessories = n;
                return;
            }
            if (head == "petslots" && t.Length == 3 && t[1] == "=")
            {
                PetSlots = !(t[2] == "0" || t[2].ToLowerInvariant() == "false" || t[2].ToLowerInvariant() == "off");
                return;
            }
            if ((head == "minionbonus" || head == "maxpets" || head == "maxlightpets") && t.Length == 3 && t[1] == "=")
            {
                int n = int.Parse(t[2], CultureInfo.InvariantCulture);
                if (head == "minionbonus")
                {
                    if (n < 0 || n > 200) throw new Exception("minionBonus must be between 0 and 200");
                    MinionBonus = n;
                }
                else
                {
                    if (n < 1 || n > 40) throw new Exception(head + " must be between 1 and 40");
                    if (head == "maxpets") MaxPets = n; else MaxLightPets = n;
                }
                return;
            }
            if ((head == "sundialcooldown" || head == "moondialcooldown") && t.Length == 3 && t[1] == "=")
            {
                int days = int.Parse(t[2], CultureInfo.InvariantCulture);
                if (days < 0 || days > 1000) throw new Exception("cooldown must be between 0 and 1000 days");
                if (head == "sundialcooldown") SundialDays = days; else MoondialDays = days;
                return;
            }
            if (head == "static" && t.Length == 4 && t[2] == "=")
            {
                int dot = t[1].LastIndexOf('.');
                if (dot <= 0) throw new Exception("expected Type.field");
                Type type = game.GetType(t[1].Substring(0, dot), false);
                if (type == null) throw new Exception("unknown type " + t[1].Substring(0, dot));
                FieldInfo f = type.GetField(t[1].Substring(dot + 1), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (f == null || f.IsLiteral || f.IsInitOnly) throw new Exception("unknown or read-only static field " + t[1]);
                Statics.Add(new StaticRule { Key = t[1], Field = f, Value = ValueCodec.Convert(t[3], f.FieldType) });
                return;
            }
            if ((head == "item" || head == "npc") && t.Length == 5)
            {
                bool isItem = head == "item";
                Type target = isItem ? itemType : npcType;
                FieldInfo f = target.GetField(t[2], BindingFlags.Instance | BindingFlags.Public);
                if (f == null || f.IsInitOnly) throw new Exception("unknown field '" + t[2] + "' on " + target.Name);
                if (!ValueCodec.IsSupported(f.FieldType)) throw new Exception("field '" + t[2] + "' has unsupported type " + f.FieldType.Name);
                var rule = new FieldRule { Field = f, Op = t[3] };
                if (rule.Op != "=" && rule.Op != "*=" && rule.Op != "+=") throw new Exception("operator must be =, *= or +=");
                if (rule.Op == "=") rule.Value = ValueCodec.Convert(t[4], f.FieldType);
                else
                {
                    if (f.FieldType == typeof(bool)) throw new Exception("*= and += do not apply to true/false fields");
                    rule.Number = double.Parse(t[4], CultureInfo.InvariantCulture);
                }
                int id;
                if (int.TryParse(t[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                {
                    var map = isItem ? ItemById : NpcById;
                    List<FieldRule> list;
                    if (!map.TryGetValue(id, out list)) map[id] = list = new List<FieldRule>();
                    list.Add(rule);
                }
                else
                {
                    rule.Selector = t[1].ToLowerInvariant();
                    if (isItem && Array.IndexOf(ItemSelectorNames, rule.Selector) < 0) throw new Exception("unknown item selector '" + t[1] + "'");
                    if (!isItem && Array.IndexOf(NpcSelectorNames, rule.Selector) < 0) throw new Exception("unknown npc selector '" + t[1] + "'");
                    (isItem ? ItemSelectors : NpcSelectors).Add(rule);
                }
                return;
            }
            throw new Exception("could not understand '" + line + "'");
        }

        public static readonly string[] ItemSelectorNames = { "*", "stackable", "consumable", "weapon", "ammo", "accessory", "tool" };
        public static readonly string[] NpcSelectorNames = { "*", "boss", "hostile", "friendly", "town" };

        static string[] Tokenize(string line)
        {
            // Split on whitespace but keep "*=" / "+=" / "=" as separate tokens even when written without spaces.
            line = line.Replace("*=", " *= ").Replace("+=", " += ");
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                bool partOfOp = c == '=' && i > 0 && (line[i - 1] == '*' || line[i - 1] == '+');
                if (c == '=' && !partOfOp) sb.Append(" = ");
                else sb.Append(c);
            }
            return sb.ToString().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }

    sealed class StaticRule
    {
        public string Key;
        public FieldInfo Field;
        public object Value;
    }

    sealed class FieldRule
    {
        public string Selector; // null for id rules
        public FieldInfo Field;
        public string Op;
        public object Value;     // for "="
        public double Number;    // for "*=" and "+="

        public void Apply(object target)
        {
            if (Op == "=") { Field.SetValue(target, Value); return; }
            double cur = System.Convert.ToDouble(Field.GetValue(target), CultureInfo.InvariantCulture);
            double next = Op == "*=" ? cur * Number : cur + Number;
            Field.SetValue(target, ValueCodec.FromDouble(next, Field.FieldType));
        }
    }

    static class ValueCodec
    {
        public static bool IsSupported(Type t) =>
            t == typeof(int) || t == typeof(float) || t == typeof(double) || t == typeof(bool) || t == typeof(byte) ||
            t == typeof(short) || t == typeof(ushort) || t == typeof(long) || t == typeof(sbyte) || t == typeof(uint);

        public static object Convert(string s, Type t)
        {
            if (t == typeof(bool))
            {
                string l = s.ToLowerInvariant();
                if (l == "true" || l == "1" || l == "yes") return true;
                if (l == "false" || l == "0" || l == "no") return false;
                throw new Exception("expected true or false");
            }
            double d = double.Parse(s, CultureInfo.InvariantCulture);
            return FromDouble(d, t);
        }

        public static object FromDouble(double d, Type t)
        {
            if (t == typeof(float)) return (float)d;
            if (t == typeof(double)) return d;
            double r = Math.Round(d);
            if (t == typeof(int)) return (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, r));
            if (t == typeof(long)) return (long)r;
            if (t == typeof(short)) return (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, r));
            if (t == typeof(ushort)) return (ushort)Math.Max(0, Math.Min(ushort.MaxValue, r));
            if (t == typeof(byte)) return (byte)Math.Max(0, Math.Min(255, r));
            if (t == typeof(sbyte)) return (sbyte)Math.Max(sbyte.MinValue, Math.Min(sbyte.MaxValue, r));
            if (t == typeof(uint)) return (uint)Math.Max(0, Math.Min(uint.MaxValue, r));
            if (t == typeof(bool)) return d != 0;
            throw new Exception("unsupported type " + t.Name);
        }

        public static string Format(object v)
        {
            if (v is bool) return (bool)v ? "1" : "0";
            if (v is float) return ((float)v).ToString("R", CultureInfo.InvariantCulture);
            if (v is double) return ((double)v).ToString("R", CultureInfo.InvariantCulture);
            return System.Convert.ToString(v, CultureInfo.InvariantCulture);
        }
    }
}
