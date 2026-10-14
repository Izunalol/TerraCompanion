using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TerraCompanion.Core.Assets;

namespace TerraCompanion.App;

public static class Imaging
{
    static readonly ConcurrentDictionary<int, ImageSource?> Icons = new();

    public static BitmapSource ToBitmap(TextureData t) => ToBitmap(t.Bgra, t.Width, t.Height);

    public static BitmapSource ToBitmap(byte[] bgra, int w, int h)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, bgra, w * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Frozen, cached item icon (safe to call from any thread).</summary>
    public static ImageSource? ItemIcon(int id)
    {
        if (id <= 0 || AppState.Game == null) return null;
        return Icons.GetOrAdd(id, i =>
        {
            var t = AppState.Game.GetItemIcon(i);
            return t == null ? null : ToBitmap(t);
        });
    }

    public static void ClearCache() => Icons.Clear();

    public static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}

/// <summary>Binding converter: item id -> icon.</summary>
public sealed class ItemIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int id ? Imaging.ItemIcon(id) : null;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}
