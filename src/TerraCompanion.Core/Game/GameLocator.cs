using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TerraCompanion.Core.Game;

public sealed record SaveFileEntry(string Path, string Display, bool IsCloud);

/// <summary>Finds the Terraria install and the save folders.</summary>
public static class GameLocator
{
    public const int SteamAppId = 105600;

    public static string DefaultSaveDir =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Terraria");

    public static string CompanionDir(string saveDir) => System.IO.Path.Combine(saveDir, "TerraCompanion");
    public static string RulesPath(string saveDir) => System.IO.Path.Combine(CompanionDir(saveDir), "rules.txt");
    public static string BackupDir(string saveDir) => System.IO.Path.Combine(CompanionDir(saveDir), "Backups");

    public static bool IsGameDir(string? dir) => !string.IsNullOrEmpty(dir) && File.Exists(System.IO.Path.Combine(dir, "Terraria.exe"));

    /// <summary>Looks for steamapps/common/Terraria in every Steam library.</summary>
    public static string? FindGameDir()
    {
        foreach (var lib in SteamLibraries())
        {
            var dir = System.IO.Path.Combine(lib, "steamapps", "common", "Terraria");
            if (IsGameDir(dir)) return dir;
        }
        foreach (var guess in new[] { @"C:\Program Files (x86)\Steam\steamapps\common\Terraria", @"C:\Program Files\Steam\steamapps\common\Terraria",
                                      @"C:\GOG Games\Terraria", @"C:\Program Files (x86)\GOG Galaxy\Games\Terraria" })
            if (IsGameDir(guess)) return guess;
        return null;
    }

    public static string? SteamRoot()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return null;
            var p = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                    ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
            return p?.Replace('/', '\\');
        }
        catch { return null; }
    }

    public static IEnumerable<string> SteamLibraries()
    {
        var root = SteamRoot();
        if (root == null) yield break;
        yield return root;
        var vdf = System.IO.Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
            yield return m.Groups[1].Value.Replace(@"\\", @"\");
    }

    public static IEnumerable<SaveFileEntry> FindPlayers(string saveDir) => Find(saveDir, "Players", "players", "*.plr");
    public static IEnumerable<SaveFileEntry> FindWorlds(string saveDir) => Find(saveDir, "Worlds", "worlds", "*.wld");

    static IEnumerable<SaveFileEntry> Find(string saveDir, string localSub, string cloudSub, string pattern)
    {
        var local = System.IO.Path.Combine(saveDir, localSub);
        if (Directory.Exists(local))
            foreach (var f in Directory.GetFiles(local, pattern).OrderByDescending(File.GetLastWriteTime))
                yield return new SaveFileEntry(f, System.IO.Path.GetFileNameWithoutExtension(f), false);

        var root = SteamRoot();
        var userdata = root == null ? null : System.IO.Path.Combine(root, "userdata");
        if (userdata == null || !Directory.Exists(userdata)) yield break;
        foreach (var user in Directory.GetDirectories(userdata))
        {
            var cloud = System.IO.Path.Combine(user, SteamAppId.ToString(), "remote", cloudSub);
            if (!Directory.Exists(cloud)) continue;
            foreach (var f in Directory.GetFiles(cloud, pattern).OrderByDescending(File.GetLastWriteTime))
                yield return new SaveFileEntry(f, System.IO.Path.GetFileNameWithoutExtension(f) + "  (Steam Cloud)", true);
        }
    }

    public static bool IsGameRunning()
    {
        try { return System.Diagnostics.Process.GetProcessesByName("Terraria").Length + System.Diagnostics.Process.GetProcessesByName("Terraria64").Length > 0; }
        catch { return false; }
    }
}
