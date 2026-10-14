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
                case PatchState.Patched: SetStatus("Patched - rules are active", version + s.Detail, "Accent"); break;
                case PatchState.PatchedOutdated: SetStatus("Patch needs updating", version + s.Detail, "Warn"); break;
                case PatchState.Vanilla: SetStatus("Not patched", version + "The game is vanilla. Patch it to enable your rules.", "Muted"); break;
                default: SetStatus("Problem", s.Detail, "Bad"); break;
            }
            PatchButton.IsEnabled = s.State != PatchState.NotFound;
            PatchButton.Content = s.State == PatchState.Vanilla ? "Patch game" : "Re-patch game";
            RestoreButton.IsEnabled = s.BackupExists && s.State != PatchState.Vanilla;
        }
        LoadLog();
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
