using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using TerraCompanion.Core.Game;
using TerraCompanion.Core.Rules;

namespace TerraCompanion.App;

public sealed class Settings
{
    public string? GameDir { get; set; }
    public string? SaveDir { get; set; }
    /// <summary>"auto" (follow Windows), "light" or "dark".</summary>
    public string Theme { get; set; } = "auto";
    /// <summary>The Launch button starts the 64-bit build (TerraCompanion64) instead of vanilla Terraria through Steam.</summary>
    public bool Launch64 { get; set; }

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TerraCompanion", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); }
        catch { return new Settings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* settings are a convenience */ }
    }
}

/// <summary>State shared by all pages: where the game is, what we know about it, and the rules being edited.</summary>
public static class AppState
{
    public static Settings Settings = Settings.Load();
    public static GameData? Game;
    public static DefaultsTable? ItemDefaults, NpcDefaults;
    public static RulesDocument Rules = new();

    public static event Action? GameChanged;
    public static event Action? RulesChanged;

    public static string? GameDir => GameLocator.IsGameDir(Settings.GameDir) ? Settings.GameDir : null;
    public static string SaveDir => string.IsNullOrWhiteSpace(Settings.SaveDir) ? GameLocator.DefaultSaveDir : Settings.SaveDir!;
    public static string RulesPath => GameLocator.RulesPath(SaveDir);
    public static string CompanionDir => GameLocator.CompanionDir(SaveDir);
    /// <summary>Writes the embedded 64-bit launcher files to a temporary folder and returns it.</summary>
    public static string ExtractLauncher64()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TerraCompanion-launcher64");
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
        var asm = typeof(AppState).Assembly;
        int n = 0;
        foreach (var name in asm.GetManifestResourceNames().Where(r => r.StartsWith("Launcher64/")))
        {
            using var res = asm.GetManifestResourceStream(name)!;
            using var file = File.Create(Path.Combine(dir, name["Launcher64/".Length..]));
            res.CopyTo(file);
            n++;
        }
        if (n == 0) throw new InvalidOperationException("The 64-bit launcher is missing from this build of TerraCompanion.");
        return dir;
    }

    /// <summary>
    /// The hooks DLL the patcher installs. Uses the copy next to the app when there is one (normal builds),
    /// otherwise extracts the copy embedded in the exe (single-file download).
    /// </summary>
    public static string HooksDll
    {
        get
        {
            var beside = Path.Combine(AppContext.BaseDirectory, "TerraCompanion.Hooks.dll");
            if (File.Exists(beside)) return beside;
            var extracted = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TerraCompanion", "TerraCompanion.Hooks.dll");
            try
            {
                using var res = typeof(AppState).Assembly.GetManifestResourceStream("TerraCompanion.Hooks.dll");
                if (res == null) return beside;
                using var ms = new MemoryStream();
                res.CopyTo(ms);
                var bytes = ms.ToArray();
                if (!File.Exists(extracted) || !File.ReadAllBytes(extracted).AsSpan().SequenceEqual(bytes))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(extracted)!);
                    File.WriteAllBytes(extracted, bytes);
                }
                return extracted;
            }
            catch { return File.Exists(extracted) ? extracted : beside; }
        }
    }

    static DispatcherTimer? _saveTimer;
    static DateTime _lastSavedStamp;

    public static void Init()
    {
        if (GameDir == null)
        {
            Settings.GameDir = GameLocator.FindGameDir();
            Settings.Save();
        }
        ReloadRules();
        ReloadDefaults();
    }

    public static async Task LoadGameAsync()
    {
        Game = null;
        var dir = GameDir;
        if (dir != null)
            Game = await Task.Run(() => GameData.Load(Path.Combine(dir, "Terraria.exe")));
        GameChanged?.Invoke();
    }

    /// <summary>Notifies pages that game data is unavailable (e.g. Terraria.exe could not be read).</summary>
    public static void GameChangedFallback()
    {
        Game = null;
        GameChanged?.Invoke();
    }

    public static void ReloadDefaults()
    {
        ItemDefaults = DefaultsTable.TryLoad(Path.Combine(CompanionDir, "items.tsv"));
        NpcDefaults = DefaultsTable.TryLoad(Path.Combine(CompanionDir, "npcs.tsv"));
    }

    public static void ReloadRules()
    {
        Rules = RulesDocument.Load(RulesPath);
        _lastSavedStamp = File.Exists(RulesPath) ? File.GetLastWriteTimeUtc(RulesPath) : default;
        RulesChanged?.Invoke();
    }

    /// <summary>True when rules.txt was edited outside the app since we last read/wrote it.</summary>
    public static bool RulesChangedOnDisk => File.Exists(RulesPath) && File.GetLastWriteTimeUtc(RulesPath) != _lastSavedStamp;

    /// <summary>Call after changing <see cref="Rules"/>; the file is written shortly after (the game reloads it live).</summary>
    public static void RulesEdited(object? source = null)
    {
        _saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) => { _saveTimer!.Stop(); SaveRulesNow(); }, Dispatcher.CurrentDispatcher);
        _saveTimer.Stop();
        _saveTimer.Start();
        RulesChanged?.Invoke();
    }

    public static void SaveRulesNow()
    {
        _saveTimer?.Stop();
        Rules.Save(RulesPath);
        _lastSavedStamp = File.GetLastWriteTimeUtc(RulesPath);
    }

    public static void Flush()
    {
        if (_saveTimer?.IsEnabled == true) SaveRulesNow();
        Settings.Save();
    }
}
