using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using TerraCompanion.Core.Game;
using TerraCompanion.Core.Saves;
using Path = System.IO.Path;
using RenderOptions = TerraCompanion.Core.Game.RenderOptions;

namespace TerraCompanion.App.Views;

public partial class WorldView : UserControl, IPage
{
    sealed record HighlightOption(string Label, ushort? Tile)
    {
        public override string ToString() => Label;
    }

    sealed record ChestHit(ChestInfo Chest, string Label)
    {
        public override string ToString() => Label;
    }

    WorldFile? _world;
    BitmapSource? _bitmap;
    Dictionary<long, ChestInfo> _chestAt = new();
    HashSet<ChestInfo> _found = new();
    ChestInfo? _selectedChest;
    double _scale = 1, _ox, _oy;
    Point? _dragStart;
    double _dragOx, _dragOy;
    bool _suppressHighlight;
    int _loadVersion;

    public WorldView()
    {
        InitializeComponent();
        ChestSearch.ToolTip = "Type an item name, e.g. 'Enchanted Sword'";
    }

    public void OnShown()
    {
        if (WorldList.Items.Count == 0) RefreshList();
    }

    void RefreshList()
    {
        WorldList.ItemsSource = GameLocator.FindWorlds(AppState.SaveDir).ToList();
    }

    void RefreshList_Click(object sender, RoutedEventArgs e) => RefreshList();

    void WorldList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WorldList.SelectedItem is SaveFileEntry entry) _ = LoadWorld(entry.Path);
    }

    void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Terraria worlds (*.wld)|*.wld|Backups (*.bak)|*.bak|All files|*.*", InitialDirectory = Path.Combine(AppState.SaveDir, "Worlds") };
        if (dlg.ShowDialog() == true) _ = LoadWorld(dlg.FileName);
    }

    // ---------------------------------------------------------------- loading & rendering

    async Task LoadWorld(string path)
    {
        int version = ++_loadVersion;
        LoadingPanel.Visibility = Visibility.Visible;
        LoadingText.Text = $"Loading {Path.GetFileNameWithoutExtension(path)}...";
        LoadingBar.Value = 0;
        var progress = new Progress<double>(p => { if (version == _loadVersion) LoadingBar.Value = p; });
        try
        {
            var world = await Task.Run(() => WorldFile.Load(path, true, progress));
            if (version != _loadVersion) return;
            _world = world;
            _chestAt = new();
            foreach (var c in world.Chests)
                for (int dx = 0; dx < 2; dx++)
                    for (int dy = 0; dy < 2; dy++)
                        _chestAt[Key(c.X + dx, c.Y + dy)] = c;
            _selectedChest = null;
            BuildInfo();
            BuildHighlightOptions();
            ChestSearch_TextChanged(this, null!);
            LoadingText.Text = "Rendering...";
            await Render();
            Fit();
            Placeholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            if (version == _loadVersion) InfoText.Text = "Could not read this world:\n" + ex.Message;
        }
        finally
        {
            if (version == _loadVersion) LoadingPanel.Visibility = Visibility.Collapsed;
        }
    }

    async Task Render()
    {
        if (_world == null) return;
        var w = _world;
        var colors = AppState.Game?.Map ?? new MapColors();
        var opts = new RenderOptions
        {
            Tiles = ShowTiles.IsChecked == true, Walls = ShowWalls.IsChecked == true, Liquids = ShowLiquids.IsChecked == true,
            Background = ShowBackground.IsChecked == true,
            Highlight = (HighlightBox.SelectedItem as HighlightOption)?.Tile is ushort t ? new HashSet<ushort> { t } : null
        };
        var pixels = await Task.Run(() => WorldRenderer.Render(w, colors, opts));
        if (w != _world) return;
        var bmp = BitmapSource.Create(w.Width, w.Height, 96, 96, PixelFormats.Bgr32, null, pixels, w.Width * 4);
        bmp.Freeze();
        _bitmap = bmp;
        MapImage.Source = bmp;
        MapImage.Width = w.Width;
        MapImage.Height = w.Height;
        ApplyTransform();
    }

    void BuildInfo()
    {
        var w = _world!;
        var sb = new StringBuilder();
        sb.AppendLine(w.Name);
        sb.AppendLine($"Size: {w.Width} × {w.Height} ({SizeName(w.Width)})");
        sb.AppendLine($"Mode: {WorldFile.GameModeName(w.GameMode)}{(w.HardMode ? " · Hardmode" : "")} · {(w.Crimson ? "Crimson" : "Corruption")}");
        if (w.Seed.Length > 0) sb.AppendLine($"Seed: {w.Seed}");
        var seeds = w.SpecialSeeds().ToList();
        if (seeds.Count > 0) sb.AppendLine("Special: " + string.Join(", ", seeds));
        if (w.Created is { } c) sb.AppendLine($"Created: {c:d}");
        if (w.LastPlayed is { } lp) sb.AppendLine($"Last played: {lp:g}");
        sb.AppendLine($"Chests: {w.Chests.Count} · File version {w.Version}");
        var downed = w.Downed.Where(k => k.Value).Select(k => k.Key).ToList();
        if (downed.Count > 0) sb.Append("Defeated / done: " + string.Join(", ", downed));
        if (w.FlagsError != null) sb.Append("\n(some header details could not be read)");
        InfoText.Text = sb.ToString().TrimEnd();
    }

    static string SizeName(int width) => width switch { <= 4200 => "small", <= 6400 => "medium", <= 8400 => "large", _ => "custom" };

    void BuildHighlightOptions()
    {
        var w = _world!;
        var counts = new int[ushort.MaxValue + 1];
        foreach (var t in w.Tiles) if (t != WorldFile.NoTile) counts[t]++;
        var g = AppState.Game;
        var options = new List<HighlightOption> { new("(none)", null) };
        options.AddRange(Enumerable.Range(0, counts.Length).Where(i => counts[i] > 0)
            .Select(i => (id: i, name: g?.TileName(i) ?? $"Tile {i}", count: counts[i]))
            .OrderBy(x => x.name)
            .Select(x => new HighlightOption($"{x.name}  ({x.count:N0})", (ushort)x.id)));
        _suppressHighlight = true;
        HighlightBox.ItemsSource = options;
        HighlightBox.SelectedIndex = 0;
        _suppressHighlight = false;
    }

    async void Layer_Click(object sender, RoutedEventArgs e) => await Render();

    async void Highlight_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressHighlight) await Render();
    }

    void Markers_Click(object sender, RoutedEventArgs e) => UpdateOverlay();

    // ---------------------------------------------------------------- chest search

    void ChestSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _found.Clear();
        var q = ChestSearch.Text.Trim();
        var g = AppState.Game;
        if (_world == null || q.Length < 2 || g == null) { ChestResults.ItemsSource = null; UpdateOverlay(); return; }
        var hits = new List<ChestHit>();
        foreach (var c in _world.Chests)
        {
            var matches = c.Items.Where(i => g.ItemName(i.Type).Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) continue;
            _found.Add(c);
            string what = string.Join(", ", matches.Select(i => (i.Stack > 1 ? $"{i.Stack}× " : "") + g.ItemName(i.Type)).Distinct().Take(3));
            hits.Add(new ChestHit(c, $"{Position(c.X, c.Y)}: {what}"));
        }
        ChestResults.ItemsSource = hits;
        UpdateOverlay();
    }

    void ChestResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChestResults.SelectedItem is not ChestHit hit) return;
        _selectedChest = hit.Chest;
        CenterOn(hit.Chest.X + 1, hit.Chest.Y + 1, Math.Max(_scale, 6));
        ShowHover(hit.Chest.X, hit.Chest.Y);
    }

    // ---------------------------------------------------------------- view transform

    void Fit()
    {
        if (_world == null || Viewport.ActualWidth <= 0) return;
        _scale = Math.Min(Viewport.ActualWidth / _world.Width, Viewport.ActualHeight / _world.Height);
        _ox = (Viewport.ActualWidth - _world.Width * _scale) / 2;
        _oy = (Viewport.ActualHeight - _world.Height * _scale) / 2;
        ApplyTransform();
    }

    void CenterOn(double tx, double ty, double scale)
    {
        _scale = scale;
        _ox = Viewport.ActualWidth / 2 - tx * _scale;
        _oy = Viewport.ActualHeight / 2 - ty * _scale;
        ApplyTransform();
    }

    void ApplyTransform()
    {
        MapImage.RenderTransform = new MatrixTransform(_scale, 0, 0, _scale, _ox, _oy);
        UpdateOverlay();
    }

    void Fit_Click(object sender, RoutedEventArgs e) => Fit();
    void GoSpawn_Click(object sender, RoutedEventArgs e) { if (_world != null) CenterOn(_world.SpawnX, _world.SpawnY, 4); }
    void GoDungeon_Click(object sender, RoutedEventArgs e) { if (_world != null) CenterOn(_world.DungeonX, _world.DungeonY, 4); }

    void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_world != null && e.PreviousSize.Width == 0) Fit(); else UpdateOverlay();
    }

    void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_world == null) return;
        var p = e.GetPosition(Viewport);
        double fit = Math.Min(Viewport.ActualWidth / _world.Width, Viewport.ActualHeight / _world.Height);
        double next = Math.Clamp(_scale * Math.Pow(1.25, e.Delta / 120.0), fit * 0.5, 40);
        // keep the tile under the cursor fixed
        _ox = p.X - (p.X - _ox) * next / _scale;
        _oy = p.Y - (p.Y - _oy) * next / _scale;
        _scale = next;
        ApplyTransform();
    }

    void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Viewport);
        _dragOx = _ox; _dragOy = _oy;
        Viewport.CaptureMouse();
    }

    void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is { } s && (e.GetPosition(Viewport) - s).Length < 3 && _world != null)
        {
            // a click (not a drag) selects the chest under the cursor
            var (tx, ty) = ToTile(e.GetPosition(Viewport));
            _selectedChest = _chestAt.GetValueOrDefault(Key(tx, ty));
            UpdateOverlay();
        }
        _dragStart = null;
        Viewport.ReleaseMouseCapture();
    }

    void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Viewport);
        if (_dragStart is { } s && e.LeftButton == MouseButtonState.Pressed)
        {
            _ox = _dragOx + (p.X - s.X);
            _oy = _dragOy + (p.Y - s.Y);
            ApplyTransform();
            return;
        }
        var (tx, ty) = ToTile(p);
        ShowHover(tx, ty);
    }

    void Viewport_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_dragStart == null) HoverPanel.Visibility = Visibility.Collapsed;
    }

    (int, int) ToTile(Point p) => ((int)Math.Floor((p.X - _ox) / _scale), (int)Math.Floor((p.Y - _oy) / _scale));

    static long Key(int x, int y) => ((long)x << 32) | (uint)y;

    // ---------------------------------------------------------------- overlay & hover

    void UpdateOverlay()
    {
        Overlay.Children.Clear();
        if (_world == null) return;
        Point S(double tx, double ty) => new(_ox + tx * _scale, _oy + ty * _scale);

        void Marker(double tx, double ty, Brush fill, string label)
        {
            var p = S(tx, ty);
            var dot = new Ellipse { Width = 12, Height = 12, Fill = fill, Stroke = Brushes.Black, StrokeThickness = 1.5 };
            Canvas.SetLeft(dot, p.X - 6); Canvas.SetTop(dot, p.Y - 6);
            Overlay.Children.Add(dot);
            var text = new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), Padding = new Thickness(3, 0, 3, 0) };
            Canvas.SetLeft(text, p.X + 9); Canvas.SetTop(text, p.Y - 8);
            Overlay.Children.Add(text);
        }

        double w = Viewport.ActualWidth, h = Viewport.ActualHeight;
        bool Visible(Point p) => p.X >= -20 && p.Y >= -20 && p.X <= w + 20 && p.Y <= h + 20;

        IEnumerable<ChestInfo> chests = ShowChests.IsChecked == true ? _world.Chests : _found;
        int drawn = 0;
        foreach (var c in chests)
        {
            var p = S(c.X + 1, c.Y + 1);
            if (!Visible(p) || ++drawn > 3000) continue;
            bool hit = _found.Contains(c);
            double size = Math.Max(5, Math.Min(2 * _scale, 14));
            var r = new Rectangle { Width = size, Height = size, Fill = hit ? Brushes.Gold : new SolidColorBrush(Color.FromRgb(0xC8, 0x8A, 0x3A)), Stroke = Brushes.Black, StrokeThickness = 1 };
            Canvas.SetLeft(r, p.X - size / 2); Canvas.SetTop(r, p.Y - size / 2);
            Overlay.Children.Add(r);
        }
        if (_selectedChest is { } sel)
        {
            var p = S(sel.X, sel.Y);
            var ring = new Rectangle { Width = 2 * _scale + 8, Height = 2 * _scale + 8, Stroke = Brushes.White, StrokeThickness = 2 };
            Canvas.SetLeft(ring, p.X - 4); Canvas.SetTop(ring, p.Y - 4);
            Overlay.Children.Add(ring);
        }
        Marker(_world.SpawnX, _world.SpawnY, Brushes.LimeGreen, "Spawn");
        if (_world.DungeonX > 0) Marker(_world.DungeonX, _world.DungeonY, Brushes.DeepSkyBlue, "Dungeon");
    }

    string Position(int x, int y)
    {
        var w = _world!;
        int east = x * 2 - w.Width;
        int depth = (int)(y * 2 - w.WorldSurface * 2);
        string layer = y > w.Height - 204 ? "Underworld" : y > w.RockLayer ? "Caverns" : y > w.WorldSurface ? "Underground" : y < w.WorldSurface * 0.35 ? "Space" : "Surface";
        string ew = east == 0 ? "Center" : $"{Math.Abs(east)}' {(east > 0 ? "East" : "West")}";
        string depthText = depth == 0 ? "Level" : $"{Math.Abs(depth)}' {(depth > 0 ? "below" : "above")}";
        return $"{ew}, {depthText} ({layer})";
    }

    void ShowHover(int x, int y)
    {
        var w = _world;
        var g = AppState.Game;
        if (w == null || x < 0 || y < 0 || x >= w.Width || y >= w.Height) { HoverPanel.Visibility = Visibility.Collapsed; return; }
        int i = w.Index(x, y);
        var sb = new StringBuilder();
        sb.AppendLine($"Tile ({x}, {y})  {Position(x, y)}");
        ushort t = w.Tiles[i];
        if (t != WorldFile.NoTile) sb.AppendLine("Block:  " + (g?.TileName(t) ?? t.ToString()) + $"  [{t}]");
        if (w.Walls[i] != 0) sb.AppendLine("Wall:   " + (g?.WallName(w.Walls[i]) ?? w.Walls[i].ToString()) + $"  [{w.Walls[i]}]");
        if (w.Liquids[i] != 0) sb.AppendLine("Liquid: " + (Liquid)w.Liquids[i]);
        var chest = _chestAt.GetValueOrDefault(Key(x, y));
        if (chest != null && g != null)
        {
            sb.AppendLine();
            sb.AppendLine(chest.Name.Length > 0 ? $"Chest \"{chest.Name}\"" : "Chest");
            if (chest.Items.Count == 0) sb.AppendLine("  (empty)");
            foreach (var it in chest.Items.Take(20))
            {
                string prefix = g.PrefixName(it.Prefix);
                sb.AppendLine($"  {(it.Stack > 1 ? it.Stack + "× " : "")}{(prefix.Length > 0 ? prefix + " " : "")}{g.ItemName(it.Type)}");
            }
            if (chest.Items.Count > 20) sb.AppendLine($"  ... and {chest.Items.Count - 20} more");
        }
        HoverText.Text = sb.ToString().TrimEnd();
        HoverPanel.Visibility = Visibility.Visible;
    }

    // ---------------------------------------------------------------- export

    void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_bitmap == null || _world == null) return;
        var dlg = new SaveFileDialog { Filter = "PNG image|*.png", FileName = _world.Name + ".png" };
        if (dlg.ShowDialog() != true) return;
        using var fs = File.Create(dlg.FileName);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(_bitmap));
        enc.Save(fs);
        MainWindow.Instance?.SetStatus("Map exported to " + dlg.FileName);
    }
}
