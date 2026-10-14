using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TerraCompanion.Core.Saves;

namespace TerraCompanion.App.Controls;

public enum SlotKind { Inventory, Bank, VoidVault, Equipment, Dye, Misc }

/// <summary>One Terraria-style inventory square bound to an <see cref="ItemSlot"/>.</summary>
public sealed class ItemSlotControl : Border
{
    public ItemSlot Slot { get; }
    public SlotKind Kind { get; }
    public string Label { get; }
    public bool AllowFavorite { get; }

    readonly Image _icon = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(4) };
    readonly TextBlock _stack = new() { FontSize = 11, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 3, 1) };
    readonly TextBlock _star = new() { Text = "★", FontSize = 10, Foreground = Brushes.Gold, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(3, 1, 0, 0) };
    readonly TextBlock _hint = new() { FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x60, 0x70)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    bool _selected;

    static readonly Brush Normal = new SolidColorBrush(Color.FromRgb(0x2A, 0x3A, 0x6B));
    static readonly Brush NormalBorder = new SolidColorBrush(Color.FromRgb(0x3F, 0x55, 0x9A));
    static readonly Brush Fav = new SolidColorBrush(Color.FromRgb(0x5A, 0x4A, 0x1E));
    static readonly Brush SelectedBorder = new SolidColorBrush(Color.FromRgb(0xE8, 0xC3, 0x5A));

    public event Action<ItemSlotControl>? Picked;
    public event Action<ItemSlotControl>? Cleared;

    public ItemSlotControl(ItemSlot slot, SlotKind kind, string label, bool allowFavorite, string emptyHint = "")
    {
        Slot = slot; Kind = kind; Label = label; AllowFavorite = allowFavorite;
        Width = Height = 46;
        Margin = new Thickness(2);
        CornerRadius = new CornerRadius(5);
        BorderThickness = new Thickness(2);
        Cursor = Cursors.Hand;
        _hint.Text = emptyHint;
        RenderOptions.SetBitmapScalingMode(_icon, BitmapScalingMode.NearestNeighbor);
        var g = new Grid();
        g.Children.Add(_hint); g.Children.Add(_icon); g.Children.Add(_stack); g.Children.Add(_star);
        Child = g;
        MouseLeftButtonUp += (_, _) => Picked?.Invoke(this);
        MouseRightButtonUp += (_, e) =>
        {
            // Right-click clears the slot, like throwing it away.
            Slot.Clear();
            Refresh();
            Cleared?.Invoke(this);
            e.Handled = true;
        };
        Refresh();
    }

    public bool Selected { get => _selected; set { _selected = value; Refresh(); } }

    public void Refresh()
    {
        var g = AppState.Game;
        bool has = !Slot.IsEmpty;
        _icon.Source = has ? Imaging.ItemIcon(Slot.Type) : null;
        _stack.Text = has && Slot.Stack > 1 ? Slot.Stack.ToString() : "";
        _star.Visibility = has && Slot.Favorited ? Visibility.Visible : Visibility.Collapsed;
        _hint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        Background = has && Slot.Favorited ? Fav : Normal;
        BorderBrush = _selected ? SelectedBorder : NormalBorder;
        string name = !has || g == null ? "" : (g.PrefixName(Slot.Prefix) is { Length: > 0 } p ? p + " " : "") + g.ItemName(Slot.Type);
        ToolTip = has ? $"{name}{(Slot.Stack > 1 ? $"  ×{Slot.Stack}" : "")}\n{Label}\nRight-click to clear" : $"{Label} (empty)";
    }
}
