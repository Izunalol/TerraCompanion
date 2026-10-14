using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TerraCompanion.App;

/// <summary>
/// Developer aid: "TerraCompanion.exe --snapshot &lt;dir&gt;" opens every page off-screen with real data,
/// writes a PNG of each, and exits. Read-only: nothing is saved or patched.
/// </summary>
static class Snapshot
{
    public static async Task Run(MainWindow w, string dir)
    {
        Directory.CreateDirectory(dir);
        while (AppState.Game == null && AppState.GameDir != null) await Task.Delay(100);
        foreach (var page in new[] { "game", "tweaks", "items", "npcs", "world", "player", "raw" })
        {
            w.Navigate(page);
            await Settle(600);
            var content = (FrameworkElement)w.FindName("Host");
            if (page == "items" || page == "npcs") SelectFirst(content, page == "items" ? 757 : 4);
            if (page == "world") { Select(content, "WorldList", 0); await WaitFor(() => Find<Image>(content, "MapImage")?.Source != null, 30000); }
            if (page == "player") { Select(content, "PlayerList", 0); await Settle(800); }
            await Settle(800);
            Save(w, Path.Combine(dir, page + ".png"));
            if (page == "player")
                foreach (var (tab, name) in new[] { (1, "player_inventory"), (2, "player_equipment") })
                {
                    if (Find<TabControl>(content, "Tabs") is { } tabs) tabs.SelectedIndex = tab;
                    await Settle(500);
                    Save(w, Path.Combine(dir, name + ".png"));
                }
        }
        w.Close();
    }

    static void SelectFirst(FrameworkElement root, int id)
    {
        var list = FindAll<ListBox>(root).FirstOrDefault();
        if (list?.ItemsSource is IEnumerable<Views.EntityEntry> items)
            list.SelectedItem = items.FirstOrDefault(e => e.Id == id) ?? items.FirstOrDefault();
    }

    static void Select(FrameworkElement root, string name, int index)
    {
        if (Find<ListBox>(root, name) is { } lb && lb.Items.Count > index) lb.SelectedIndex = index;
    }

    static T? Find<T>(DependencyObject root, string name) where T : FrameworkElement =>
        FindAll<T>(root).FirstOrDefault(e => e.Name == name);

    static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var x in FindAll<T>(c)) yield return x;
        }
    }

    static async Task Settle(int ms)
    {
        await Task.Delay(ms);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    static async Task WaitFor(Func<bool> cond, int timeoutMs)
    {
        for (int t = 0; t < timeoutMs && !cond(); t += 100) await Task.Delay(100);
    }

    static void Save(Window w, string path)
    {
        var root = (FrameworkElement)w.Content;
        var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(w.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
