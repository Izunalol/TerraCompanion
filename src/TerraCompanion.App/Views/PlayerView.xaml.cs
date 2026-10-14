using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using TerraCompanion.App.Controls;
using TerraCompanion.Core.Game;
using TerraCompanion.Core.Saves;

namespace TerraCompanion.App.Views;

public partial class PlayerView : UserControl, IPage
{
    public sealed record PlayerEntry(string Path, string Title, string Sub, bool IsCloud);

    public sealed class ItemResult
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public ImageSource? Icon => Imaging.ItemIcon(Id);
    }

    PlayerFile? _player;
    PlayerEntry? _entry;
    bool _dirty, _loadingSlot;
    ItemSlotControl? _selected;
    readonly List<ItemSlotControl> _slots = new();
    List<ItemResult> _allItems = new();

    public PlayerView() => InitializeComponent();

    public void OnShown()
    {
        if (PlayerList.Items.Count == 0) _ = RefreshList();
    }

    // ---------------------------------------------------------------- list

    async Task RefreshList()
    {
        var saveDir = AppState.SaveDir;
        var entries = await Task.Run(() => GameLocator.FindPlayers(saveDir).Select(f =>
        {
            try
            {
                var (name, diff, _) = PlayerFile.Peek(f.Path);
                return new PlayerEntry(f.Path, name, $"{PlayerFile.DifficultyName(diff)} · {System.IO.Path.GetFileName(f.Path)}{(f.IsCloud ? " · Steam Cloud" : "")}", f.IsCloud);
            }
            catch (Exception e) { return new PlayerEntry(f.Path, System.IO.Path.GetFileNameWithoutExtension(f.Path), "Unreadable: " + e.Message, f.IsCloud); }
        }).ToList());
        PlayerList.ItemsSource = entries;
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshList();

    void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Terraria characters (*.plr)|*.plr|Backups|*.bak;*.plr|All files|*.*", InitialDirectory = System.IO.Path.Combine(AppState.SaveDir, "Players") };
        if (dlg.ShowDialog() == true && ConfirmDiscard()) LoadPlayer(new PlayerEntry(dlg.FileName, System.IO.Path.GetFileNameWithoutExtension(dlg.FileName), dlg.FileName, false));
    }

    void PlayerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlayerList.SelectedItem is not PlayerEntry entry || entry == _entry) return;
        if (!ConfirmDiscard()) { PlayerList.SelectedItem = _entry; return; }
        LoadPlayer(entry);
    }

    bool ConfirmDiscard() => !_dirty || MessageBox.Show("Discard unsaved changes to this character?", "Unsaved changes",
        MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    void LoadPlayer(PlayerEntry entry)
    {
        try { _player = PlayerFile.Load(entry.Path); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not open character", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _entry = entry;
        if (_allItems.Count == 0 && AppState.Game is { } g)
            _allItems = Enumerable.Range(1, g.ItemCount - 1).Select(i => new ItemResult { Id = i, Name = g.ItemName(i) }).ToList();
        BuildAll();
        SetDirty(false);
        EmptyText.Visibility = Visibility.Collapsed;
        EditorRoot.Visibility = Visibility.Visible;
        var warnings = new List<string>();
        if (entry.IsCloud) warnings.Add("This is a Steam Cloud character. Steam may sync over your edits - consider moving it to local saves in-game first.");
        if (_player.Release < (AppState.Game?.Release ?? 0)) warnings.Add($"Saved by an older game version (release {_player.Release}); it is written back in the same format and the game upgrades it on load.");
        WarningText.Text = string.Join("\n", warnings);
        WarningBar.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void BuildAll()
    {
        _slots.Clear();
        _selected = null;
        BuildStats();
        BuildInventory();
        BuildEquipment();
        BuildStorage();
        BuildBuffs();
        UpdateHeader();
        ShowSlot(null);
    }

    void UpdateHeader()
    {
        if (_player == null) return;
        HeaderName.Text = _player.Name + (_dirty ? "  •" : "");
        HeaderSub.Text = $"{PlayerFile.DifficultyName(_player.Difficulty)} · {TimeSpan.FromTicks(_player.PlayTimeTicks):%d'd 'hh'h 'mm'm'} played · release {_player.Release} · {_player.Path}";
    }

    void SetDirty(bool dirty)
    {
        _dirty = dirty;
        SaveButton.IsEnabled = RevertButton.IsEnabled = dirty;
        UpdateHeader();
    }

    void Changed() => SetDirty(true);

    void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (_entry != null) { _dirty = false; LoadPlayer(_entry); }
    }

    async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_player == null) return;
        Keyboard.ClearFocus(); // commit the focused text box
        if (GameLocator.IsGameRunning() && MessageBox.Show(
                "Terraria is running. If this character is currently loaded in the game, the game will overwrite your edits when it next saves.\n\nSave anyway?",
                "Terraria is running", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            string? backup = _player.Save(backupDir: GameLocator.BackupDir(AppState.SaveDir));
            SetDirty(false);
            MainWindow.Instance?.SetStatus($"Saved {_player.Name}." + (backup != null ? $" Backup: {backup}" : ""));
            await RefreshList();
            PlayerList.SelectedItem = (PlayerList.ItemsSource as IEnumerable<PlayerEntry>)?.FirstOrDefault(p => p.Path == _player.Path);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Saving failed; the original file was not changed.\n\n" + ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------------------------------------------------------------- stats tab

    void BuildStats()
    {
        var p = _player!;
        var s = StatsPanel;
        s.Children.Clear();

        s.Children.Add(H2("Character"));
        s.Children.Add(TextRow("Name", p.Name, v => { p.Name = v; UpdateHeader(); }));
        s.Children.Add(ComboRow("Difficulty", new[] { "Classic", "Mediumcore", "Hardcore", "Journey" }, Math.Min((int)p.Difficulty, 3), i => p.Difficulty = (byte)i,
            "Switching a non-Journey character to Journey (or back) is allowed by the file format, but Journey characters can only join Journey worlds."));

        s.Children.Add(H2("Health & mana"));
        s.Children.Add(IntRow("Max life", p.StatLifeMax, v => p.StatLifeMax = v, 100, 500, "The game caps this at 500 when loading (400 from Life Crystals + 100 from Life Fruit)."));
        s.Children.Add(IntRow("Current life", p.StatLife, v => p.StatLife = v, 1, 9999));
        s.Children.Add(IntRow("Max mana", p.StatManaMax, v => p.StatManaMax = v, 20, 200, "The game caps this at 200 when loading."));
        s.Children.Add(IntRow("Current mana", p.StatMana, v => p.StatMana = v, 0, 400));
        var max = new Button { Content = "Max out life, mana and permanent upgrades", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        max.Click += (_, _) =>
        {
            p.StatLifeMax = p.StatLife = 500; p.StatManaMax = p.StatMana = 200;
            p.ExtraAccessory = p.UnlockedBiomeTorches = p.AteArtisanBread = true;
            if (p.Release >= 260) p.UsedAegisCrystal = p.UsedAegisFruit = p.UsedArcaneCrystal = p.UsedGalaxyPearl = p.UsedGummyWorm = p.UsedAmbrosia = true;
            Changed(); BuildStats();
        };
        s.Children.Add(max);

        s.Children.Add(H2("Permanent upgrades"));
        s.Children.Add(Check("Demon Heart (extra accessory slot)", p.ExtraAccessory, v => p.ExtraAccessory = v));
        s.Children.Add(Check("Torch God's Favor (biome torches unlocked)", p.UnlockedBiomeTorches, v => p.UnlockedBiomeTorches = v));
        s.Children.Add(Check("Biome torch swap enabled", p.UsingBiomeTorches, v => p.UsingBiomeTorches = v));
        if (p.Release >= 256) s.Children.Add(Check("Artisan Loaf (+1 crafting range)", p.AteArtisanBread, v => p.AteArtisanBread = v));
        if (p.Release >= 260)
        {
            s.Children.Add(Check("Vital Crystal (life regen)", p.UsedAegisCrystal, v => p.UsedAegisCrystal = v));
            s.Children.Add(Check("Aegis Fruit (+4 defense)", p.UsedAegisFruit, v => p.UsedAegisFruit = v));
            s.Children.Add(Check("Arcane Crystal (mana regen)", p.UsedArcaneCrystal, v => p.UsedArcaneCrystal = v));
            s.Children.Add(Check("Galaxy Pearl (+luck)", p.UsedGalaxyPearl, v => p.UsedGalaxyPearl = v));
            s.Children.Add(Check("Gummy Worm (fishing power)", p.UsedGummyWorm, v => p.UsedGummyWorm = v));
            s.Children.Add(Check("Ambrosia (faster mining & building)", p.UsedAmbrosia, v => p.UsedAmbrosia = v));
        }
        s.Children.Add(Check("Defeated the Old One's Army (Tavernkeep shop)", p.DownedDD2EventAnyDifficulty, v => p.DownedDD2EventAnyDifficulty = v));

        s.Children.Add(H2("Appearance"));
        s.Children.Add(IntRow("Hair style", p.Hair, v => p.Hair = v, 0, 300));
        s.Children.Add(IntRow("Skin / body variant", p.SkinVariant, v => p.SkinVariant = (byte)v, 0, 255));
        var colors = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        colors.Children.Add(ColorSwatch("Hair", () => p.HairColor, c => p.HairColor = c));
        colors.Children.Add(ColorSwatch("Skin", () => p.SkinColor, c => p.SkinColor = c));
        colors.Children.Add(ColorSwatch("Eyes", () => p.EyeColor, c => p.EyeColor = c));
        colors.Children.Add(ColorSwatch("Shirt", () => p.ShirtColor, c => p.ShirtColor = c));
        colors.Children.Add(ColorSwatch("Undershirt", () => p.UnderShirtColor, c => p.UnderShirtColor = c));
        colors.Children.Add(ColorSwatch("Pants", () => p.PantsColor, c => p.PantsColor = c));
        colors.Children.Add(ColorSwatch("Shoes", () => p.ShoeColor, c => p.ShoeColor = c));
        s.Children.Add(colors);

        s.Children.Add(H2("Progress"));
        s.Children.Add(IntRow("Angler quests finished", p.AnglerQuestsFinished, v => p.AnglerQuestsFinished = v, 0, 100000));
        s.Children.Add(IntRow("Golfer score", p.GolferScoreAccumulated, v => p.GolferScoreAccumulated = v, 0, 10_000_000));
        s.Children.Add(IntRow("Tax money waiting (copper)", p.TaxMoney, v => p.TaxMoney = v, 0, int.MaxValue));
        if (p.Release >= 254)
        {
            s.Children.Add(IntRow("Deaths (PvE)", p.NumberOfDeathsPVE, v => p.NumberOfDeathsPVE = v, 0, int.MaxValue));
            s.Children.Add(IntRow("Deaths (PvP)", p.NumberOfDeathsPVP, v => p.NumberOfDeathsPVP = v, 0, int.MaxValue));
        }
        if (p.Dead)
            s.Children.Add(Check("Currently dead (uncheck to revive instantly)", p.Dead, v => { p.Dead = v; if (!v) p.RespawnTimer = 0; }));
    }

    TextBlock H2(string text) => new() { Text = text, Style = (Style)FindResource("H2") };

    Grid LabeledRow(string label, FrameworkElement editor, string hint = "")
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(editor, 1);
        g.Children.Add(editor);
        if (hint.Length > 0)
        {
            var h = new TextBlock { Text = hint, Style = (Style)FindResource("Hint"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(h, 2);
            g.Children.Add(h);
        }
        return g;
    }

    FrameworkElement TextRow(string label, string value, Action<string> set)
    {
        var box = new TextBox { Text = value, MaxLength = 20 };
        box.TextChanged += (_, _) => { set(box.Text); Changed(); };
        return LabeledRow(label, box);
    }

    FrameworkElement IntRow(string label, int value, Action<int> set, int min, int max, string hint = "")
    {
        var box = new TextBox { Text = value.ToString(CultureInfo.InvariantCulture) };
        void Commit()
        {
            if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            {
                v = Math.Clamp(v, min, max);
                box.Text = v.ToString(CultureInfo.InvariantCulture);
                set(v); Changed();
            }
            else box.Text = value.ToString(CultureInfo.InvariantCulture);
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        return LabeledRow(label, box, hint);
    }

    FrameworkElement ComboRow(string label, string[] options, int index, Action<int> set, string hint = "")
    {
        var cb = new ComboBox { ItemsSource = options, SelectedIndex = index };
        cb.SelectionChanged += (_, _) => { set(cb.SelectedIndex); Changed(); UpdateHeader(); };
        return LabeledRow(label, cb, hint);
    }

    CheckBox Check(string label, bool value, Action<bool> set)
    {
        var cb = new CheckBox { Content = label, IsChecked = value };
        cb.Click += (_, _) => { set(cb.IsChecked == true); Changed(); };
        return cb;
    }

    FrameworkElement ColorSwatch(string label, Func<Rgb> get, Action<Rgb> set)
    {
        var c = get();
        var swatch = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(4), BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1),
                                  Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), Cursor = Cursors.Hand };
        var sp = new StackPanel { Margin = new Thickness(0, 0, 14, 6), ToolTip = "Click to change" };
        sp.Children.Add(swatch);
        sp.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = (Brush)FindResource("Muted"), HorizontalAlignment = HorizontalAlignment.Center });
        swatch.MouseLeftButtonUp += (_, _) =>
        {
            var cur = get();
            using var dlg = new System.Windows.Forms.ColorDialog { Color = System.Drawing.Color.FromArgb(cur.R, cur.G, cur.B), FullOpen = true, AnyColor = true };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            var n = new Rgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
            set(n);
            swatch.Background = new SolidColorBrush(Color.FromRgb(n.R, n.G, n.B));
            Changed();
        };
        return sp;
    }

    // ---------------------------------------------------------------- slots

    ItemSlotControl Slot(ItemSlot slot, SlotKind kind, string label, bool fav, string hint = "")
    {
        var c = new ItemSlotControl(slot, kind, label, fav, hint);
        c.Picked += SelectSlot;
        c.Cleared += sc => { SelectSlot(sc); Changed(); };
        _slots.Add(c);
        return c;
    }

    void SelectSlot(ItemSlotControl sc)
    {
        if (_selected != null) _selected.Selected = false;
        _selected = sc;
        sc.Selected = true;
        ShowSlot(sc);
    }

    WrapPanel Row(params UIElement[] items)
    {
        var w = new WrapPanel();
        foreach (var i in items) w.Children.Add(i);
        return w;
    }

    void BuildInventory()
    {
        var p = _player!;
        InventoryPanel.Children.Clear();
        TextBlock Caption(string text, double top) => new() { Text = text, Style = (Style)FindResource("Hint"), Margin = new Thickness(2, top, 0, 2) };

        InventoryPanel.Children.Add(Caption("Hotbar", 0));
        InventoryPanel.Children.Add(Row(Enumerable.Range(0, 10).Select(i => (UIElement)Slot(p.Inventory[i], SlotKind.Inventory, $"Hotbar {(i + 1) % 10}", true, ((i + 1) % 10).ToString())).ToArray()));
        InventoryPanel.Children.Add(Caption("Inventory", 8));
        for (int r = 1; r < 5; r++)
            InventoryPanel.Children.Add(Row(Enumerable.Range(r * 10, 10).Select(i => (UIElement)Slot(p.Inventory[i], SlotKind.Inventory, $"Inventory slot {i + 1}", true)).ToArray()));

        // Coins and ammo sit under the grid (instead of beside it) so the page fits next to the slot editor.
        var extras = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var coins = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
        coins.Children.Add(Caption("Coins", 0));
        coins.Children.Add(Row(Enumerable.Range(50, 4).Select(i => (UIElement)Slot(p.Inventory[i], SlotKind.Inventory, $"Coin slot {i - 49}", true, "coin")).ToArray()));
        var ammo = new StackPanel();
        ammo.Children.Add(Caption("Ammo", 0));
        ammo.Children.Add(Row(Enumerable.Range(54, 4).Select(i => (UIElement)Slot(p.Inventory[i], SlotKind.Inventory, $"Ammo slot {i - 53}", true, "ammo")).ToArray()));
        extras.Children.Add(coins);
        extras.Children.Add(ammo);
        InventoryPanel.Children.Add(extras);
        InventoryPanel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Hint"), Margin = new Thickness(2, 12, 0, 0),
            Text = "Click a slot to edit it on the right. Right-click a slot to clear it."
        });
    }

    void BuildEquipment()
    {
        var p = _player!;
        bool fav = p.Release >= 322;
        EquipmentPanel.Children.Clear();
        string[] names = { "Head", "Body", "Legs", "Accessory 1", "Accessory 2", "Accessory 3", "Accessory 4", "Accessory 5", "Accessory 6", "Accessory 7" };
        var header = new Grid();
        void Header(string t, int col, int width)
        {
            var tb = new TextBlock { Text = t, Style = (Style)FindResource("Hint"), Width = width, TextAlignment = TextAlignment.Center };
            Grid.SetColumn(tb, col); header.Children.Add(tb);
        }
        for (int i = 0; i < 4; i++) header.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 3 ? new GridLength(1, GridUnitType.Star) : new GridLength(50) });
        Header("Dye", 0, 50); Header("Vanity", 1, 50); Header("Equipped", 2, 50);
        EquipmentPanel.Children.Add(header);
        for (int i = 0; i < 10; i++)
        {
            string note = i == 8 ? "  (needs Demon Heart, Expert+)" : i == 9 ? "  (Master Mode only)" : "";
            var row = Row(
                Slot(p.Dye[i], SlotKind.Dye, names[i] + " dye", fav, "dye"),
                Slot(p.Armor[i + 10], SlotKind.Equipment, names[i] + " (vanity)", fav, "social"),
                Slot(p.Armor[i], SlotKind.Equipment, names[i], fav, i < 3 ? names[i].ToLower() : "acc"),
                new TextBlock { Text = names[i] + note, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Foreground = (Brush)FindResource("Muted") });
            EquipmentPanel.Children.Add(row);
            if (i == 2) EquipmentPanel.Children.Add(new Border { Height = 10 });
        }

        EquipmentPanel.Children.Add(H2("Equipment slots (pets, mounts, hooks)"));
        string[] misc = { "Pet", "Light pet", "Minecart", "Mount", "Grappling hook" };
        for (int i = 0; i < 5; i++)
            EquipmentPanel.Children.Add(Row(
                Slot(p.MiscDyes[i], SlotKind.Dye, misc[i] + " dye", false, "dye"),
                Slot(p.MiscEquips[i], SlotKind.Misc, misc[i], false, misc[i].Split(' ')[0].ToLower()),
                new TextBlock { Text = misc[i], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Foreground = (Brush)FindResource("Muted") }));
        EquipmentPanel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Hint"), Margin = new Thickness(2, 12, 0, 0),
            Text = "This is the currently active loadout. The other two equipment loadouts are kept unchanged."
        });
    }

    void BuildStorage()
    {
        var p = _player!;
        StoragePanel.Children.Clear();
        void Bank(string title, ItemSlot[] items, SlotKind kind, bool fav)
        {
            StoragePanel.Children.Add(H2(title));
            for (int r = 0; r < 4; r++)
                StoragePanel.Children.Add(Row(Enumerable.Range(r * 10, 10).Select(i => (UIElement)Slot(items[i], kind, $"{title} slot {i + 1}", fav)).ToArray()));
        }
        Bank("Piggy Bank", p.Bank, SlotKind.Bank, false);
        Bank("Safe", p.Bank2, SlotKind.Bank, false);
        Bank("Defender's Forge", p.Bank3, SlotKind.Bank, false);
        Bank("Void Vault", p.Bank4, SlotKind.VoidVault, p.Release >= 255);
    }

    // ---------------------------------------------------------------- slot editor

    void ShowSlot(ItemSlotControl? sc)
    {
        var g = AppState.Game;
        _loadingSlot = true;
        try
        {
            bool has = sc != null;
            ItemSearch.IsEnabled = has;
            SlotTitle.Text = sc?.Label ?? "Click a slot";
            var slot = sc?.Slot;
            SlotIcon.Source = slot is { IsEmpty: false } ? Imaging.ItemIcon(slot.Type) : null;
            SlotItemName.Text = slot is { IsEmpty: false } && g != null ? g.ItemName(slot.Type) + $"  (#{slot.Type})" : "Empty";
            bool stackable = sc != null && sc.Kind is SlotKind.Inventory or SlotKind.Bank or SlotKind.VoidVault;
            StackBox.IsEnabled = MaxStackButton.IsEnabled = stackable && slot is { IsEmpty: false };
            StackBox.Text = slot is { IsEmpty: false } ? slot.Stack.ToString(CultureInfo.InvariantCulture) : "";
            FavoriteBox.IsEnabled = sc?.AllowFavorite == true && slot is { IsEmpty: false };
            FavoriteBox.IsChecked = slot?.Favorited == true;
            if (PrefixBox.ItemsSource == null && g != null)
                PrefixBox.ItemsSource = new[] { "(none)" }.Concat(Enumerable.Range(1, Math.Max(0, g.PrefixCount - 1)).Select(i => g.PrefixName(i))).ToList();
            PrefixBox.IsEnabled = slot is { IsEmpty: false };
            PrefixBox.SelectedIndex = slot != null && PrefixBox.Items.Count > slot.Prefix ? slot.Prefix : 0;
        }
        finally { _loadingSlot = false; }
    }

    void SlotChanged()
    {
        _selected?.Refresh();
        Changed();
        ShowSlot(_selected);
    }

    void StackBox_Commit(object sender, RoutedEventArgs e)
    {
        if (_loadingSlot || _selected == null || _selected.Slot.IsEmpty) return;
        if (int.TryParse(StackBox.Text.Trim(), out int v) && v > 0 && v != _selected.Slot.Stack)
        {
            _selected.Slot.Stack = Math.Min(v, int.MaxValue);
            SlotChanged();
        }
        else StackBox.Text = _selected.Slot.Stack.ToString(CultureInfo.InvariantCulture);
    }

    void StackBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) StackBox_Commit(sender, e);
    }

    void MaxStack_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || _selected.Slot.IsEmpty) return;
        int max = (int)(AppState.ItemDefaults?.Num(_selected.Slot.Type, "maxStack") ?? 0);
        _selected.Slot.Stack = max > 0 ? max : 9999;
        SlotChanged();
    }

    void PrefixBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSlot || _selected == null || _selected.Slot.IsEmpty || PrefixBox.SelectedIndex < 0) return;
        _selected.Slot.Prefix = (byte)PrefixBox.SelectedIndex;
        SlotChanged();
    }

    void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        _selected.Slot.Favorited = FavoriteBox.IsChecked == true;
        SlotChanged();
    }

    void ClearSlot_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        _selected.Slot.Clear();
        SlotChanged();
    }

    void ItemSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = ItemSearch.Text.Trim();
        ItemSearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (q.Length == 0) { ItemResults.ItemsSource = null; return; }
        IEnumerable<ItemResult> hits = int.TryParse(q, out int id)
            ? _allItems.Where(i => i.Id == id || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            : _allItems.Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                       .OrderBy(i => i.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(i => i.Name.Length);
        ItemResults.ItemsSource = hits.Take(300).ToList();
    }

    void ItemResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemResults.SelectedItem is not ItemResult r || _selected == null) return;
        var s = _selected.Slot;
        bool wasEmpty = s.IsEmpty;
        s.Type = r.Id;
        s.Prefix = 0;
        if (wasEmpty || s.Stack <= 0) s.Stack = 1;
        if (_selected.Kind is SlotKind.Equipment or SlotKind.Dye or SlotKind.Misc) s.Stack = 1;
        ItemResults.SelectedItem = null;
        SlotChanged();
    }

    // ---------------------------------------------------------------- buffs

    void BuildBuffs()
    {
        var p = _player!;
        var g = AppState.Game;
        BuffsPanel.Children.Clear();
        BuffsPanel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Hint"), FontSize = 13, Margin = new Thickness(0, 0, 0, 8),
            Text = $"Buffs saved with the character (up to {p.BuffCount}). Some buffs are removed by the game on load if they don't make sense outside a world."
        });
        for (int i = 0; i < p.BuffCount; i++)
        {
            if (p.BuffType[i] <= 0) continue;
            int idx = i;
            var time = new TextBox { Width = 90, Text = (p.BuffTime[i] / 60.0).ToString("0.#", CultureInfo.InvariantCulture) };
            time.LostFocus += (_, _) =>
            {
                if (double.TryParse(time.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs) && secs > 0)
                { p.BuffTime[idx] = (int)Math.Min(int.MaxValue, secs * 60); Changed(); }
            };
            var remove = new Button { Content = "Remove", Style = (Style)FindResource("Danger"), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(8, 0, 0, 0) };
            remove.Click += (_, _) => { p.BuffType[idx] = 0; p.BuffTime[idx] = 0; Changed(); BuildBuffs(); };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(new TextBlock { Text = g?.BuffName(p.BuffType[i]) ?? p.BuffType[i].ToString(), Width = 260, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(time);
            row.Children.Add(new TextBlock { Text = "seconds", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Muted") });
            row.Children.Add(remove);
            BuffsPanel.Children.Add(row);
        }

        BuffsPanel.Children.Add(H2("Add a buff"));
        var options = g == null ? new List<(int id, string name)>() :
            Enumerable.Range(1, Math.Max(0, g.BuffCount - 1)).Select(i => (id: i, name: g.BuffName(i))).OrderBy(x => x.name).ToList();
        var pick = new ComboBox { Width = 260, ItemsSource = options.Select(o => $"{o.name}  (#{o.id})").ToList() };
        var secs = new TextBox { Width = 90, Text = "600", Margin = new Thickness(8, 0, 0, 0) };
        var add = new Button { Content = "Add", Margin = new Thickness(8, 0, 0, 0) };
        add.Click += (_, _) =>
        {
            if (pick.SelectedIndex < 0) return;
            int slot = Array.FindIndex(p.BuffType, 0, p.BuffCount, t => t <= 0);
            if (slot < 0) { MessageBox.Show("All buff slots are full."); return; }
            double.TryParse(secs.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var s);
            p.BuffType[slot] = options[pick.SelectedIndex].id;
            p.BuffTime[slot] = (int)Math.Max(60, s * 60);
            Changed(); BuildBuffs();
        };
        var addRow = new StackPanel { Orientation = Orientation.Horizontal };
        addRow.Children.Add(pick); addRow.Children.Add(secs);
        addRow.Children.Add(new TextBlock { Text = "seconds", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Muted") });
        addRow.Children.Add(add);
        BuffsPanel.Children.Add(addRow);
    }

    void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs) return;
        bool itemTab = Tabs.SelectedIndex is 1 or 2 or 3;
        SlotEditor.Visibility = itemTab ? Visibility.Visible : Visibility.Collapsed;
    }
}
