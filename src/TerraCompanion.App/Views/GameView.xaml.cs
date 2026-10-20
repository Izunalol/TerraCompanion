using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using TerraCompanion.Core.Game;

namespace TerraCompanion.App.Views;

public partial class GameView : UserControl, IPage
{
    public GameView()
    {
        InitializeComponent();
        AppState.GameChanged += () => Dispatcher.Invoke(Refresh);
    }

    public void OnShown() => Refresh();

    string? Exe => AppState.GameDir is { } d ? Path.Combine(d, "Terraria.exe") : null;
    string? ServerExe => AppState.GameDir is { } d && File.Exists(Path.Combine(d, "TerrariaServer.exe")) ? Path.Combine(d, "TerrariaServer.exe") : null;

    void Refresh()
    {
        GameDirBox.Text = AppState.GameDir ?? "(not found)";
        SaveDirBox.Text = AppState.SaveDir;
        ServerCheck.IsEnabled = ServerExe != null;

        if (Exe == null)
        {
            SetStatus("Terraria not found", "Use Detect or Browse to point at the folder that contains Terraria.exe.", "Bad");
            PatchButton.IsEnabled = RestoreButton.IsEnabled = false;
        }
        else
        {
            var s = Patcher.GetStatus(Exe, AppState.HooksDll);
            var version = AppState.Game != null ? $"Terraria {AppState.Game.Version} (release {AppState.Game.Release}). " : "";
            switch (s.State)
            {
                case PatchState.Patched: SetStatus("Patched - rules are active", version + s.Detail, "Good"); break;
                case PatchState.PatchedOutdated: SetStatus("Patch needs updating", version + s.Detail, "Warn"); break;
                case PatchState.Vanilla: SetStatus("Not patched", version + "The game is vanilla. Patch it to enable your rules.", "Muted"); break;
                default: SetStatus("Problem", s.Detail, "Bad"); break;
            }
            PatchButton.IsEnabled = s.State != PatchState.NotFound;
            PatchButton.Content = s.State == PatchState.Vanilla ? "Patch game" : "Re-patch game";
            RestoreButton.IsEnabled = s.BackupExists && s.State != PatchState.Vanilla;
        }
        Refresh64();
        LoadLog();
    }

    // ---------------------------------------------------------------- 64-bit build

    void Refresh64()
    {
        var dir = AppState.GameDir;
        string? tml = dir == null ? null : Port64.FindTModLoader(dir);
        bool runtime = Port64.DesktopRuntimeInstalled();
        bool built = dir != null && Port64.IsBuilt(dir);
        Build64Button.IsEnabled = dir != null && tml != null;
        Build64Button.Content = built ? "Rebuild 64-bit version" : "Build 64-bit version";
        Launch64Button.IsEnabled = built && runtime;
        Remove64Button.IsEnabled = built;

        string brush, text;
        if (dir == null) { brush = "Muted"; text = "Point TerraCompanion at your Terraria folder first."; }
        else if (tml == null) { brush = "Bad"; text = "Needs tModLoader installed through Steam (next to Terraria): the 64-bit build uses its FNA and 64-bit Steam libraries. You don't have to play it."; }
        else if (!runtime) { brush = "Warn"; text = "Needs the .NET 8 Desktop Runtime (x64), free from Microsoft: dotnet.microsoft.com/download/dotnet/8.0 → \".NET Desktop Runtime\" → Windows x64."; }
        else if (!built) { brush = "Muted"; text = "Not built yet. Building takes a few seconds and adds a TerraCompanion64 folder to the game folder."; }
        else if (Port64.NeedsRebuild(dir)) { brush = "Warn"; text = "Terraria.exe changed since the 64-bit version was built (re-patch or Steam update). Rebuild it to match."; }
        else { brush = "Good"; text = "Ready. Launch it here; Steam shows it as Terraria."; }
        Status64Dot.Fill = (Brush)FindResource(brush);
        Status64Detail.Text = text;
        Prefer64Check.IsChecked = AppState.Settings.Launch64;
        Prefer64Check.IsEnabled = built || AppState.Settings.Launch64;
        LaunchButton.Content = AppState.Settings.Launch64 && built ? "▶ Launch Terraria (64-bit)" : "▶ Launch Terraria";
    }

    void Prefer64_Click(object sender, RoutedEventArgs e)
    {
        AppState.Settings.Launch64 = Prefer64Check.IsChecked == true;
        AppState.Settings.Save();
        Refresh64();
    }

    async void Build64_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.GameDir is not { } dir || Port64.FindTModLoader(dir) is not { } tml) return;
        if (!EnsureGameClosed()) return;
        Build64Button.IsEnabled = false;
        Log64.Text = "Building the 64-bit version...";
        try
        {
            Log64.Text = await Task.Run(() => Port64.Build(dir, tml, AppState.ExtractLauncher64()));
            MainWindow.Instance?.SetStatus("64-bit version built.");
        }
        catch (UnauthorizedAccessException) { Log64.Text = "Access denied writing to the game folder - run TerraCompanion as administrator once."; }
        catch (Exception ex) { Log64.Text = "Build failed: " + ex.Message; }
        Refresh64();
    }

    void Launch64_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.GameDir is not { } dir) return;
        if (GameLocator.IsGameRunning()) { MessageBox.Show("Terraria is already running.", "Terraria is running"); return; }
        if (Port64.NeedsRebuild(dir) && MessageBox.Show("Terraria.exe changed since the 64-bit version was built (re-patch or Steam update). Launch the old 64-bit build anyway?",
                "Rebuild recommended", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var outDir = Port64.OutputDir(dir);
        Process.Start(new ProcessStartInfo(Path.Combine(outDir, Port64.LauncherExe)) { WorkingDirectory = outDir, UseShellExecute = false });
    }

    void Remove64_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.GameDir is not { } dir || !EnsureGameClosed()) return;
        try { Port64.Remove(dir); Log64.Text = "Removed the 64-bit version (your game and its Content folder are untouched)."; }
        catch (Exception ex) { Log64.Text = "Remove failed: " + ex.Message; }
        Refresh64();
    }

    void SetStatus(string title, string detail, string brush)
    {
        StatusTitle.Text = title;
        StatusDetail.Text = detail;
        StatusDot.Fill = (Brush)FindResource(brush);
    }

    void LoadLog()
    {
        var log = Path.Combine(AppState.CompanionDir, "hooks.log");
        try { LogBox.Text = File.Exists(log) ? File.ReadAllText(log) : "No log yet - it appears after you start the patched game."; }
        catch (IOException) { LogBox.Text = "(log is in use by the game)"; }
        LogBox.ScrollToEnd();
    }

    bool EnsureGameClosed()
    {
        if (!GameLocator.IsGameRunning()) return true;
        MessageBox.Show("Please close Terraria first - Windows does not allow replacing a running program.", "Terraria is running", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    async void Patch_Click(object sender, RoutedEventArgs e)
    {
        if (Exe == null || !EnsureGameClosed()) return;
        if (!File.Exists(AppState.HooksDll))
        {
            MessageBox.Show("TerraCompanion.Hooks.dll is missing next to TerraCompanion.exe. Rebuild or re-download the app.", "Missing file");
            return;
        }
        var exe = Exe; var server = ServerCheck.IsChecked == true ? ServerExe : null;
        PatchButton.IsEnabled = false;
        PatchLog.Text = "Patching...";
        try
        {
            var log = await Task.Run(() =>
            {
                var l = Patcher.Patch(exe, AppState.HooksDll);
                if (server != null) l += Environment.NewLine + "--- TerrariaServer.exe" + Environment.NewLine + Patcher.Patch(server, AppState.HooksDll);
                return l;
            });
            // keep the 64-bit copy in step with the freshly patched game
            if (AppState.GameDir is { } gd && Port64.IsBuilt(gd) && Port64.FindTModLoader(gd) is { } tml)
            {
                try { await Task.Run(() => Port64.Build(gd, tml, AppState.ExtractLauncher64())); log += Environment.NewLine + "Rebuilt the 64-bit version."; }
                catch (Exception ex) { log += Environment.NewLine + "64-bit rebuild failed: " + ex.Message; }
            }
            PatchLog.Text = log;
            if (!File.Exists(AppState.RulesPath)) AppState.SaveRulesNow();
            MainWindow.Instance?.SetStatus("Game patched. Launch Terraria and your rules are live.");
        }
        catch (UnauthorizedAccessException)
        {
            PatchLog.Text = "Access denied. The game folder is write-protected - run TerraCompanion as administrator once, or move the Steam library.";
        }
        catch (Exception ex) { PatchLog.Text = "Patch failed: " + ex.Message; }
        Refresh();
    }

    void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (Exe == null || !EnsureGameClosed()) return;
        try
        {
            var log = Patcher.Restore(Exe);
            if (ServerExe is { } s && File.Exists(Patcher.BackupPath(s))) log += Environment.NewLine + Patcher.Restore(s);
            PatchLog.Text = log;
        }
        catch (Exception ex) { PatchLog.Text = "Restore failed: " + ex.Message; }
        Refresh();
    }

    void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.Settings.Launch64 && AppState.GameDir is { } dir && Port64.IsBuilt(dir))
        {
            Launch64_Click(sender, e);
            return;
        }
        try { Process.Start(new ProcessStartInfo($"steam://rungameid/{GameLocator.SteamAppId}") { UseShellExecute = true }); }
        catch
        {
            if (Exe != null) Process.Start(new ProcessStartInfo(Exe) { WorkingDirectory = AppState.GameDir!, UseShellExecute = true });
        }
    }

    async void Detect_Click(object sender, RoutedEventArgs e)
    {
        var dir = GameLocator.FindGameDir();
        if (dir == null) { MessageBox.Show("Could not find Terraria in any Steam library. Use Browse instead."); return; }
        await SetGameDir(dir);
    }

    async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Terraria.exe|Terraria.exe", Title = "Select Terraria.exe" };
        if (dlg.ShowDialog() == true) await SetGameDir(Path.GetDirectoryName(dlg.FileName)!);
    }

    async Task SetGameDir(string dir)
    {
        AppState.Settings.GameDir = dir;
        AppState.Settings.Save();
        Imaging.ClearCache();
        await AppState.LoadGameAsync();
        Refresh();
    }

    void BrowseSaves_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select your Terraria save folder (contains Players and Worlds)", InitialDirectory = AppState.SaveDir };
        if (dlg.ShowDialog() != true) return;
        AppState.Settings.SaveDir = dlg.FolderName;
        AppState.Settings.Save();
        AppState.ReloadRules();
        AppState.ReloadDefaults();
        Refresh();
    }

    void OpenSaves_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppState.CompanionDir);
        Process.Start(new ProcessStartInfo(AppState.SaveDir) { UseShellExecute = true });
    }

    void RefreshLog_Click(object sender, RoutedEventArgs e)
    {
        AppState.ReloadDefaults();
        LoadLog();
    }
}
