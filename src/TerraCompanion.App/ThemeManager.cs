using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace TerraCompanion.App;

/// <summary>Light / Dark palettes ("Apple" look). "auto" follows the Windows app-theme setting.</summary>
public static class ThemeManager
{
    public static string Mode { get; private set; } = "auto";
    public static bool IsDark { get; private set; }

    /// <summary>Raised after the palette changed (pages that built UI in code can refresh).</summary>
    public static event Action? Changed;

    static bool _watching;

    public static bool SystemPrefersDark()
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int v && v == 0;
        }
        catch { return false; }
    }

    public static void Apply(string mode)
    {
        Mode = mode is "light" or "dark" ? mode : "auto";
        bool dark = Mode == "dark" || (Mode == "auto" && SystemPrefersDark());
        var dicts = Application.Current.Resources.MergedDictionaries;
        var uri = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative);
        bool changed = dark != IsDark || dicts.Count == 0 || dicts[0].Source?.OriginalString != uri.OriginalString;
        if (changed)
        {
            if (dicts.Count == 0) dicts.Add(new ResourceDictionary { Source = uri });
            else dicts[0] = new ResourceDictionary { Source = uri };
        }
        IsDark = dark;
        foreach (Window w in Application.Current.Windows) ApplyChrome(w);
        if (!_watching)
        {
            _watching = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && Mode == "auto")
                    Application.Current.Dispatcher.BeginInvoke(() => { Apply("auto"); });
            };
        }
        if (changed) Changed?.Invoke();
    }

    // ---- title bar: dark flag + caption colours (Windows 10 2004+ / Windows 11), silently ignored elsewhere

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void ApplyChrome(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int dark = IsDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));        // DWMWA_USE_IMMERSIVE_DARK_MODE
            if (Application.Current.TryFindResource("Bg") is SolidColorBrush bg)
            {
                int caption = bg.Color.R | bg.Color.G << 8 | bg.Color.B << 16;
                DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR
                int border = caption;
                DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));  // DWMWA_BORDER_COLOR
            }
            if (Application.Current.TryFindResource("Text") is SolidColorBrush fg)
            {
                int text = fg.Color.R | fg.Color.G << 8 | fg.Color.B << 16;
                DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));    // DWMWA_TEXT_COLOR
            }
        }
        catch { /* purely cosmetic */ }
    }
}
