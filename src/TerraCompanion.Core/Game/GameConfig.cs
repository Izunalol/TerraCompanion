using System.Text.RegularExpressions;

namespace TerraCompanion.Core.Game;

/// <summary>Reads and edits the few keys of Terraria's own config.json that TerraCompanion cares about.</summary>
public static class GameConfig
{
    static readonly Regex Support4K = new("\"Support4K\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex DisplaySize = new("\"DisplayWidth\"\\s*:\\s*(\\d+)[\\s\\S]*?\"DisplayHeight\"\\s*:\\s*(\\d+)", RegexOptions.Compiled);

    public static string Path(string saveDir) => System.IO.Path.Combine(saveDir, "config.json");

    /// <summary>The game's own 4K switch (HiDef graphics profile, 4096 px limit). Null if the key or file is missing.</summary>
    public static bool? ReadSupport4K(string saveDir)
    {
        try
        {
            var m = Support4K.Match(File.ReadAllText(Path(saveDir)));
            return m.Success ? m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase) : null;
        }
        catch { return null; }
    }

    /// <summary>Saved fullscreen / window resolution, e.g. 2560x1440.</summary>
    public static (int width, int height)? ReadResolution(string saveDir)
    {
        try
        {
            var m = DisplaySize.Match(File.ReadAllText(Path(saveDir)));
            return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Turns Support4K on, keeping the rest of the file byte-for-byte. Returns false if nothing changed
    /// (already on, key missing, or file unreadable). Only do this while the game is closed: it rewrites config.json on exit.
    /// </summary>
    public static bool EnableSupport4K(string saveDir)
    {
        try
        {
            var path = Path(saveDir);
            var text = File.ReadAllText(path);
            var m = Support4K.Match(text);
            if (!m.Success || m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase)) return false;
            File.Copy(path, path + ".tcbak", overwrite: true);
            File.WriteAllText(path, text.Remove(m.Index, m.Length).Insert(m.Index, "\"Support4K\": true"));
            return true;
        }
        catch { return false; }
    }
}
