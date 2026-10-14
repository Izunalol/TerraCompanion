namespace TerraCompanion.Core.Rules;

/// <summary>
/// Vanilla stats exported by the patched game (items.tsv / npcs.tsv in the TerraCompanion folder).
/// Only exists after the patched game has been started once.
/// </summary>
public sealed class DefaultsTable
{
    public string GameVersion = "";
    public string[] Columns = Array.Empty<string>();
    readonly Dictionary<int, string[]> _rows = new();
    readonly Dictionary<string, int> _col = new(StringComparer.Ordinal);

    public int Count => _rows.Count;

    public static DefaultsTable? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var t = new DefaultsTable();
            using var r = new StreamReader(path);
            var marker = r.ReadLine()?.Split('\t');
            if (marker is { Length: >= 2 } && marker[0] == "#version") t.GameVersion = marker[1];
            t.Columns = (r.ReadLine() ?? "").Split('\t');
            for (int i = 0; i < t.Columns.Length; i++) t._col[t.Columns[i]] = i;
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                var cells = line.Split('\t');
                if (cells.Length > 1 && int.TryParse(cells[0], out int id)) t._rows[id] = cells;
            }
            return t;
        }
        catch { return null; }
    }

    public string? Get(int id, string field) =>
        _rows.TryGetValue(id, out var row) && _col.TryGetValue(field, out int c) && c < row.Length ? row[c] : null;

    public double Num(int id, string field) =>
        double.TryParse(Get(id, field), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    public bool Flag(int id, string field) => Get(id, field) == "1";
}
