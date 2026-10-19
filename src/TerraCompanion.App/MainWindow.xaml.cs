using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TerraCompanion.App.Views;
using TerraCompanion.Core.Game;

namespace TerraCompanion.App;

/// <summary>Pages implement this to refresh when they become visible.</summary>
public interface IPage
{
    void OnShown();
}

public partial class MainWindow : Window
{
    public static MainWindow? Instance { get; private set; }
    readonly Dictionary<string, FrameworkElement> _pages = new();
    bool _themeInit;

    public MainWindow()
    {
        Instance = this;
        InitializeComponent();
        AppState.Init();
        SourceInitialized += (_, _) => ThemeManager.ApplyChrome(this);
        _themeInit = true; // showing the saved choice must not re-save or re-apply it
        foreach (ComboBoxItem i in ThemeBox.Items) if ((string)i.Tag == AppState.Settings.Theme) ThemeBox.SelectedItem = i;
        if (ThemeBox.SelectedItem == null) ThemeBox.SelectedIndex = 0;
        _themeInit = false;
        ThemeManager.Changed += RebuildPages;
        AppState.GameChanged += () => GameVersionText.Text = AppState.Game != null
            ? $"Terraria {AppState.Game.Version}"
            : "Terraria not found - set the folder on the Game page";
        Loaded += async (_, _) =>
        {
            Nav.SelectedIndex = 0;
            SetStatus("Reading game data...");
            try
            {
                await AppState.LoadGameAsync();
                SetStatus(AppState.Game != null ? $"Loaded {AppState.Game.ItemCount - 1} items, {AppState.Game.NpcNames.Count} NPCs and the map palette from Terraria.exe." : "Terraria not found.");
            }
            catch (Exception ex) { SetStatus("Could not read Terraria.exe: " + ex.Message); AppState.GameChangedFallback(); }
        };
        Closing += (_, _) => AppState.Flush();

        var timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) =>
        {
            bool running = GameLocator.IsGameRunning();
            RunningText.Text = running ? "● Terraria is running" : "○ Terraria is not running";
            RunningText.Foreground = (System.Windows.Media.Brush)FindResource(running ? "Good" : "Muted");
        }, Dispatcher);
        timer.Start();
    }

    public void SetStatus(string text) => StatusText.Text = text;

    void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not ListBoxItem item) return;
        var key = (string)item.Tag;
        if (!_pages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "game" => new GameView(),
                "tweaks" => new TweaksView(),
                "items" => new ItemsView(),
                "npcs" => new NpcsView(),
                "world" => new WorldView(),
                "player" => new PlayerView(),
                _ => new RawRulesView(),
            };
            _pages[key] = page;
        }
        Host.Content = page;
        (page as IPage)?.OnShown();
    }

    void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_themeInit || ThemeBox.SelectedItem is not ComboBoxItem item) return;
        var mode = (string)item.Tag;
        if (mode == AppState.Settings.Theme && mode == ThemeManager.Mode) return;
        AppState.Settings.Theme = mode;
        AppState.Settings.Save();
        ThemeManager.Apply(mode);
    }

    /// <summary>Pages that build part of their UI in code pick up the new palette by being recreated; an unsaved character page is kept.</summary>
    void RebuildPages()
    {
        foreach (var key in _pages.Keys.ToList())
            if (!(_pages[key] is PlayerView { HasUnsavedChanges: true })) _pages.Remove(key);
        if (Nav.SelectedItem != null) Nav_SelectionChanged(Nav, null!);
    }

    public void Navigate(string key)
    {
        foreach (ListBoxItem i in Nav.Items)
            if ((string)i.Tag == key) { Nav.SelectedItem = i; return; }
    }
}
