using System.Globalization;
using System.Text;

namespace TerraCompanion.Core.Rules;

public enum RuleOp { Set, Multiply, Add }

/// <summary>One "item|npc &lt;selector&gt; &lt;field&gt; &lt;op&gt; &lt;value&gt;" line.</summary>
public sealed record FieldRule(string Selector, string Field, RuleOp Op, string Value)
{
    public static string OpText(RuleOp op) => op switch { RuleOp.Multiply => "*=", RuleOp.Add => "+=", _ => "=" };

    /// <summary>Formats the override the way the editor shows it: "300", "x1.5", "+10".</summary>
    public string Display => Op switch { RuleOp.Multiply => "x" + Value, RuleOp.Add => "+" + Value, _ => Value };

    /// <summary>Parses the editor syntax: "300" / "-1" / "true" (set), "x1.5" or "*1.5" (multiply), "+10" or "+-10" (add).</summary>
    public static bool TryParseDisplay(string text, bool isBool, out RuleOp op, out string value)
    {
        text = text.Trim();
        op = RuleOp.Set; value = text;
        if (text.Length == 0) return false;
        if (isBool)
        {
            var l = text.ToLowerInvariant();
            value = l is "true" or "1" or "yes" or "on" ? "true" : l is "false" or "0" or "no" or "off" ? "false" : "";
            return value.Length > 0;
        }
        if (text[0] is 'x' or 'X' or '*' or '×') { op = RuleOp.Multiply; value = text[1..].Trim(); }
        else if (text[0] == '+') { op = RuleOp.Add; value = text[1..].Trim(); }
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }
}

/// <summary>
/// App-side model of rules.txt (the format GameHooks reads). Unknown lines and comments are kept,
/// so hand-written rules survive edits made through the UI.
/// </summary>
public sealed class RulesDocument
{
    public double SpawnRate = 1, MaxSpawns = 1;
    /// <summary>Enchanted Sundial / Moondial cooldown in days; null = vanilla.</summary>
    public int? SundialDays, MoondialDays;
    /// <summary>Extra minion slots (vanilla 0), and how many pets / light pets may be out at once (vanilla 1).</summary>
    public int MinionBonus, MaxPets = 1, MaxLightPets = 1;
    /// <summary>Extra equipment slots for the extra pets / light pets (on unless turned off).</summary>
    public bool PetSlots = true;
    public readonly SortedDictionary<string, string> Statics = new(StringComparer.Ordinal);
    public readonly List<FieldRule> Items = new();
    public readonly List<FieldRule> Npcs = new();
    public readonly List<string> Other = new();

    public static RulesDocument Load(string path) => File.Exists(path) ? Parse(File.ReadAllText(path)) : new RulesDocument();

    public static RulesDocument Parse(string text)
    {
        var d = new RulesDocument();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var code = line.Contains('#') ? line[..line.IndexOf('#')] : line;
            var t = Tokenize(code);
            if (t.Length == 0)
            {
                if (line.Trim().Length > 0 && !line.TrimStart().StartsWith("# TerraCompanion") && !line.TrimStart().StartsWith("# ---")) d.Other.Add(line);
                continue;
            }
            string head = t[0].ToLowerInvariant();
            if ((head == "spawnrate" || head == "maxspawns") && t.Length == 3 && t[1] == "=" && TryNum(t[2], out var mul))
            {
                if (head == "spawnrate") d.SpawnRate = mul; else d.MaxSpawns = mul;
            }
            else if (head == "petslots" && t.Length == 3 && t[1] == "=") d.PetSlots = !(t[2] == "0" || t[2].Equals("false", StringComparison.OrdinalIgnoreCase) || t[2].Equals("off", StringComparison.OrdinalIgnoreCase));
            else if ((head == "minionbonus" || head == "maxpets" || head == "maxlightpets") && t.Length == 3 && t[1] == "="
                     && int.TryParse(t[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) && count >= 0)
            {
                if (head == "minionbonus") d.MinionBonus = count;
                else if (head == "maxpets") d.MaxPets = Math.Max(1, count);
                else d.MaxLightPets = Math.Max(1, count);
            }
            else if ((head == "sundialcooldown" || head == "moondialcooldown") && t.Length == 3 && t[1] == "="
                     && int.TryParse(t[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int days) && days >= 0)
            {
                if (head == "sundialcooldown") d.SundialDays = days; else d.MoondialDays = days;
            }
            else if (head == "static" && t.Length == 4 && t[2] == "=") d.Statics[t[1]] = t[3];
            else if ((head == "item" || head == "npc") && t.Length == 5 && t[3] is "=" or "*=" or "+=")
            {
                var op = t[3] == "*=" ? RuleOp.Multiply : t[3] == "+=" ? RuleOp.Add : RuleOp.Set;
                (head == "item" ? d.Items : d.Npcs).Add(new FieldRule(t[1], t[2], op, t[4]));
            }
            else d.Other.Add(line);
        }
        return d;
    }

    static bool TryNum(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    static string[] Tokenize(string line)
    {
        line = line.Replace("*=", " *= ").Replace("+=", " += ");
        var sb = new StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            bool partOfOp = c == '=' && i > 0 && (line[i - 1] == '*' || line[i - 1] == '+');
            sb.Append(c == '=' && !partOfOp ? " = " : c.ToString());
        }
        return sb.ToString().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# TerraCompanion rules - read live by the patched game (changes apply within a second).");
        sb.AppendLine("# --- world");
        sb.AppendLine($"spawnRate = {F(SpawnRate)}");
        sb.AppendLine($"maxSpawns = {F(MaxSpawns)}");
        if (MinionBonus > 0) sb.AppendLine($"minionBonus = {MinionBonus}");
        if (MaxPets > 1) sb.AppendLine($"maxPets = {MaxPets}");
        if (MaxLightPets > 1) sb.AppendLine($"maxLightPets = {MaxLightPets}");
        if (!PetSlots) sb.AppendLine("petSlots = 0");
        if (SundialDays is int sun) sb.AppendLine($"sundialCooldown = {sun}");
        if (MoondialDays is int moon) sb.AppendLine($"moondialCooldown = {moon}");
        foreach (var (k, v) in Statics) sb.AppendLine($"static {k} = {v}");
        if (Items.Count > 0) sb.AppendLine("# --- items");
        foreach (var r in Items) sb.AppendLine($"item {r.Selector} {r.Field} {FieldRule.OpText(r.Op)} {r.Value}");
        if (Npcs.Count > 0) sb.AppendLine("# --- npcs");
        foreach (var r in Npcs) sb.AppendLine($"npc {r.Selector} {r.Field} {FieldRule.OpText(r.Op)} {r.Value}");
        if (Other.Count > 0)
        {
            sb.AppendLine("# --- other (hand-written lines are kept as-is)");
            foreach (var l in Other) sb.AppendLine(l);
        }
        return sb.ToString();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, ToText(), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    public static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------ helpers for the editors

    public FieldRule? Get(List<FieldRule> list, string selector, string field) =>
        list.LastOrDefault(r => r.Selector == selector && r.Field == field);

    /// <summary>Replaces (or removes, when <paramref name="rule"/> is null) the rule for selector+field.</summary>
    public void Set(List<FieldRule> list, string selector, string field, FieldRule? rule)
    {
        int idx = list.FindIndex(r => r.Selector == selector && r.Field == field);
        list.RemoveAll(r => r.Selector == selector && r.Field == field);
        if (rule == null) return;
        if (idx < 0 || idx > list.Count) list.Add(rule); else list.Insert(idx, rule);
    }

    public IEnumerable<string> ModifiedSelectors(List<FieldRule> list) => list.Select(r => r.Selector).Distinct();
}
