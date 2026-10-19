using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using TerraCompanion.Core.Rules;

namespace TerraCompanion.App.Views;

public enum FieldKind { Int, Float, Bool }

public sealed record FieldDef(string Name, string Label, string Group, FieldKind Kind, string Hint = "");

public sealed class EntityEntry : INotifyPropertyChanged
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string Sub { get; init; } = "";
    public bool HasIcon { get; init; }
    bool _modified;
    public bool Modified { get => _modified; set { _modified = value; PropertyChanged?.Invoke(this, new(nameof(Modified))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Searchable list of items or NPCs on the left, per-field overrides on the right.</summary>
public abstract class RuleEditorView : UserControl, IPage
{
    protected abstract string Noun { get; }               // "item" / "NPC"
    protected abstract FieldDef[] Fields { get; }
    protected abstract List<FieldRule> RuleList { get; }
    protected abstract DefaultsTable? Defaults { get; }
    protected abstract IEnumerable<EntityEntry> LoadEntries();
    protected abstract string[] Categories { get; }
    protected abstract bool InCategory(int id, string category);
    protected virtual string ExtraNote => "";
    protected virtual string DescribeValue(string field, string value) => "";

    readonly TextBox _search = new() { Margin = new Thickness(0, 0, 0, 8) };
    readonly ComboBox _category = new() { Margin = new Thickness(0, 0, 0, 8) };
    readonly ListBox _list = new();
    readonly StackPanel _detail = new();
    readonly TextBlock _count = new();
    List<EntityEntry> _all = new();
    EntityEntry? _current;

    protected RuleEditorView()
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        var left = new DockPanel();
        _search.SetValue(DockPanel.DockProperty, Dock.Top);
        _category.SetValue(DockPanel.DockProperty, Dock.Top);
        _count.SetValue(DockPanel.DockProperty, Dock.Bottom);
        _count.Style = (Style)Application.Current.FindResource("Hint");
        _count.Margin = new Thickness(0, 6, 0, 0);
        left.Children.Add(Placeholder(_search, "Search by name or ID..."));
        left.Children.Add(_category);
        left.Children.Add(_count);
        left.Children.Add(_list);
        root.Children.Add(left);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _detail };
        Grid.SetColumn(scroll, 2);
        root.Children.Add(scroll);
        Content = root;

        _list.ItemTemplate = (DataTemplate)XamlReader.Parse(@"
<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:views='clr-namespace:TerraCompanion.App.Views;assembly=TerraCompanion'>
  <DockPanel Height='28'>
    <Border DockPanel.Dock='Left' Width='28' Height='28' Margin='0,0,8,0'>
      <Image Source='{Binding Converter={x:Static views:EntityIconConverter.Instance}}' Stretch='Uniform' StretchDirection='DownOnly' RenderOptions.BitmapScalingMode='NearestNeighbor'/>
    </Border>
    <TextBlock DockPanel.Dock='Right' Text='●' Foreground='{DynamicResource Gold}' VerticalAlignment='Center' Margin='6,0,4,0' ToolTip='Has overrides'>
      <TextBlock.Style><Style TargetType='TextBlock'><Setter Property='Visibility' Value='Collapsed'/>
        <Style.Triggers><DataTrigger Binding='{Binding Modified}' Value='True'><Setter Property='Visibility' Value='Visible'/></DataTrigger></Style.Triggers></Style></TextBlock.Style>
    </TextBlock>
    <StackPanel VerticalAlignment='Center'>
      <TextBlock Text='{Binding Name}' TextTrimming='CharacterEllipsis' Foreground='{DynamicResource Text}'/>
      <TextBlock Text='{Binding Sub}' FontSize='10' Foreground='{DynamicResource Muted}'/>
    </StackPanel>
  </DockPanel>
</DataTemplate>");
        VirtualizingPanel.SetIsVirtualizing(_list, true);

        _search.TextChanged += (_, _) => ApplyFilter();
        _category.SelectionChanged += (_, _) => ApplyFilter();
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is EntityEntry e) Show(e); };
        AppState.GameChanged += () => Dispatcher.Invoke(Reload);
    }

    public void OnShown()
    {
        if (AppState.RulesChangedOnDisk) AppState.ReloadRules();
        if (Defaults == null) AppState.ReloadDefaults();
        if (_all.Count == 0) Reload();
        else { RefreshModified(); if (_current != null) Show(_current); }
    }

    void Reload()
    {
        _all = LoadEntries().ToList();
        var cat = _category.SelectedItem as string;
        _category.ItemsSource = new[] { "All", "Modified" }.Concat(Defaults != null ? Categories : Array.Empty<string>()).ToArray();
        _category.SelectedItem = cat != null && ((string[])_category.ItemsSource).Contains(cat) ? cat : "All";
        RefreshModified();
        ApplyFilter();
        if (_current == null) ShowEmpty();
    }

    void RefreshModified()
    {
        var mod = RuleList.Select(r => r.Selector).ToHashSet();
        foreach (var e in _all) e.Modified = mod.Contains(e.Id.ToString(CultureInfo.InvariantCulture));
    }

    void ApplyFilter()
    {
        string q = _search.Text.Trim();
        string cat = _category.SelectedItem as string ?? "All";
        IEnumerable<EntityEntry> items = _all;
        if (cat == "Modified") items = items.Where(e => e.Modified);
        else if (cat != "All") items = items.Where(e => InCategory(e.Id, cat));
        if (q.Length > 0)
        {
            if (int.TryParse(q, out int id)) items = items.Where(e => e.Id == id || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
            else items = items.Where(e => e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Sub.Contains(q, StringComparison.OrdinalIgnoreCase));
        }
        var list = items.ToList();
        _list.ItemsSource = list;
        _count.Text = $"{list.Count} of {_all.Count} {Noun}s";
    }

    void ShowEmpty()
    {
        _detail.Children.Clear();
        _detail.Children.Add(new TextBlock { Text = $"{char.ToUpper(Noun[0])}{Noun[1..]} Editor", Style = (Style)Application.Current.FindResource("H1") });
        _detail.Children.Add(Hint($"Pick a {Noun} on the left. Type a number to set a value (300), x1.5 to multiply, or +10 to add. Leave a box empty to keep the vanilla value. Changes are saved instantly and the patched game picks them up while you play." + ExtraNote));
        if (Defaults == null)
            _detail.Children.Add(Warning("Vanilla values are not known yet. Patch the game and start it once - it exports them automatically."));
    }

    void Show(EntityEntry e)
    {
        _current = e;
        _detail.Children.Clear();
        string sel = e.Id.ToString(CultureInfo.InvariantCulture);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var icon = Imaging.ItemIcon(e.Id);
        if (e.HasIcon && icon != null)
        {
            var img = new Image { Source = icon, Stretch = Stretch.Uniform, Width = Math.Min(64, icon.Width * 2), Height = Math.Min(64, icon.Height * 2), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            header.Children.Add(img);
        }
        var reset = new Button { Content = $"Reset {Noun}", Style = (Style)Application.Current.FindResource("Danger"), VerticalAlignment = VerticalAlignment.Center };
        reset.SetValue(DockPanel.DockProperty, Dock.Right);
        reset.Click += (_, _) => { RuleList.RemoveAll(r => r.Selector == sel); AppState.RulesEdited(this); e.Modified = false; Show(e); };
        header.Children.Add(reset);
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = e.Name, FontSize = 22, FontWeight = FontWeights.SemiBold });
        title.Children.Add(new TextBlock { Text = e.Sub, Style = (Style)Application.Current.FindResource("Hint") });
        header.Children.Add(title);
        _detail.Children.Add(header);

        if (Defaults == null)
            _detail.Children.Add(Warning("Vanilla values unknown until the patched game has been started once."));
        else if (AppState.Game != null && Defaults.GameVersion.Length > 0 && !Defaults.GameVersion.Contains(AppState.Game.Version))
            _detail.Children.Add(Warning($"Vanilla values were exported from {Defaults.GameVersion}; start the game once to refresh them."));
        if (ExtraNote.Length > 0) _detail.Children.Add(Hint(ExtraNote.Trim()));

        foreach (var group in Fields.GroupBy(f => f.Group))
        {
            _detail.Children.Add(new TextBlock { Text = group.Key, Style = (Style)Application.Current.FindResource("H2") });
            var card = new Border { Style = (Style)Application.Current.FindResource("Card"), Padding = new Thickness(12, 6, 12, 6) };
            var rows = new StackPanel();
            foreach (var f in group) rows.Children.Add(FieldRow(e, sel, f));
            card.Child = rows;
            _detail.Children.Add(card);
        }
        _detail.Children.Add(new Border { Height = 20 });
    }

    FrameworkElement FieldRow(EntityEntry e, string sel, FieldDef f)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var label = new TextBlock { Text = f.Label, VerticalAlignment = VerticalAlignment.Center, ToolTip = f.Hint.Length > 0 ? $"{f.Hint}\n\nField: {f.Name}" : $"Field: {f.Name}" };
        grid.Children.Add(label);

        string? vanilla = Defaults?.Get(e.Id, f.Name);
        var vanillaText = new TextBlock
        {
            Text = vanilla == null ? "vanilla: ?" : "vanilla: " + Pretty(f, vanilla),
            Foreground = (Brush)Application.Current.FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = vanilla == null ? null : DescribeValue(f.Name, vanilla) is { Length: > 0 } d ? d : null
        };
        Grid.SetColumn(vanillaText, 1);
        grid.Children.Add(vanillaText);

        var result = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.FindResource("Gold"), Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(result, 3);
        grid.Children.Add(result);

        var existing = AppState.Rules.Get(RuleList, sel, f.Name);
        void UpdateResult(FieldRule? r)
        {
            if (r == null) { result.Text = ""; return; }
            string? computed = Compute(f, vanilla, r);
            result.Text = computed == null ? "overridden" : "→ " + Pretty(f, computed) + (DescribeValue(f.Name, computed) is { Length: > 0 } d ? "  " + d : "");
        }
        UpdateResult(existing);

        void Store(FieldRule? r)
        {
            AppState.Rules.Set(RuleList, sel, f.Name, r);
            AppState.RulesEdited(this);
            e.Modified = RuleList.Any(x => x.Selector == sel);
            UpdateResult(r);
        }

        FrameworkElement editor;
        if (f.Kind == FieldKind.Bool)
        {
            var cb = new ComboBox { ItemsSource = new[] { "vanilla", "Yes", "No" } };
            cb.SelectedIndex = existing == null ? 0 : existing.Value == "true" ? 1 : 2;
            cb.SelectionChanged += (_, _) => Store(cb.SelectedIndex == 0 ? null : new FieldRule(sel, f.Name, RuleOp.Set, cb.SelectedIndex == 1 ? "true" : "false"));
            editor = cb;
        }
        else
        {
            var box = new TextBox { Text = existing?.Display ?? "" };
            void Commit()
            {
                var text = box.Text.Trim();
                if (text.Length == 0) { if (AppState.Rules.Get(RuleList, sel, f.Name) != null) Store(null); return; }
                if (!FieldRule.TryParseDisplay(text, false, out var op, out var value))
                {
                    box.Text = AppState.Rules.Get(RuleList, sel, f.Name)?.Display ?? "";
                    MainWindow.Instance?.SetStatus($"'{text}' is not a valid value. Use 300, x1.5 or +10.");
                    return;
                }
                if (f.Kind == FieldKind.Int && op == RuleOp.Set && !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    value = Math.Round(double.Parse(value, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture);
                var rule = new FieldRule(sel, f.Name, op, value);
                box.Text = rule.Display;
                Store(rule);
            }
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, ev) => { if (ev.Key == Key.Enter) Commit(); };
            editor = box;
        }
        Grid.SetColumn(editor, 2);
        grid.Children.Add(editor);
        return grid;
    }

    static string? Compute(FieldDef f, string? vanilla, FieldRule r)
    {
        if (r.Op == RuleOp.Set) return r.Value;
        if (vanilla == null || !double.TryParse(vanilla, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
        double n = double.Parse(r.Value, CultureInfo.InvariantCulture);
        double res = r.Op == RuleOp.Multiply ? v * n : v + n;
        return f.Kind == FieldKind.Int ? Math.Round(res).ToString(CultureInfo.InvariantCulture) : res.ToString("0.###", CultureInfo.InvariantCulture);
    }

    static string Pretty(FieldDef f, string raw) => f.Kind switch
    {
        FieldKind.Bool => raw is "1" or "true" ? "Yes" : "No",
        FieldKind.Float when double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d.ToString("0.###", CultureInfo.InvariantCulture),
        _ => raw
    };

    static TextBlock Hint(string text) => new() { Text = text, Style = (Style)Application.Current.FindResource("Hint"), FontSize = 13, Margin = new Thickness(0, 0, 0, 8) };

    static Border Warning(string text) => new()
    {
        Background = (Brush)Application.Current.FindResource("WarnBg"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8),
        Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.FindResource("Warn") }
    };

    /// <summary>Wraps a TextBox with a grey placeholder.</summary>
    internal static FrameworkElement Placeholder(TextBox box, string text)
    {
        var grid = new Grid();
        box.SetValue(DockPanel.DockProperty, Dock.Top);
        grid.SetValue(DockPanel.DockProperty, Dock.Top);
        var ph = new TextBlock { Text = text, Foreground = (Brush)Application.Current.FindResource("Muted"), Margin = new Thickness(8, 5, 0, 0), IsHitTestVisible = false };
        ph.SetBinding(VisibilityProperty, new Binding("Text") { Source = box, Converter = new EmptyToVisible() });
        grid.Children.Add(box);
        grid.Children.Add(ph);
        return grid;
    }

    sealed class EmptyToVisible : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) => string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }
}

/// <summary>Icon for an <see cref="EntityEntry"/> (items only).</summary>
public sealed class EntityIconConverter : IValueConverter
{
    public static readonly EntityIconConverter Instance = new();
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is EntityEntry { HasIcon: true } e ? Imaging.ItemIcon(e.Id) : null;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}
