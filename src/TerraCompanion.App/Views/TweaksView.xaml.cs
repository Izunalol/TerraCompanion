using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using TerraCompanion.Core.Game;
using TerraCompanion.Core.Rules;

namespace TerraCompanion.App.Views;

public partial class TweaksView : UserControl, IPage
{
    /// <summary>A UI preset that maps to one or more selector rules.</summary>
    sealed record Preset(string Label, string Hint, bool IsToggle, bool IsNpc, (string sel, string field, RuleOp op, string toggleValue)[] Rules);

    static readonly Preset[] ItemPresets =
    {
        new("Weapon damage ×", "Multiplies the damage of every weapon (pickaxes and other tools that deal damage count too).", false, false,
            new[] { ("weapon", "damage", RuleOp.Multiply, "") }),
        new("Weapon use speed ×", "Below 1 is faster: 0.5 swings/shoots twice as fast.", false, false,
            new[] { ("weapon", "useTime", RuleOp.Multiply, ""), ("weapon", "useAnimation", RuleOp.Multiply, "") }),
        new("Tool speed ×", "Pickaxes, axes and hammers. Below 1 is faster.", false, false,
            new[] { ("tool", "useTime", RuleOp.Multiply, ""), ("tool", "useAnimation", RuleOp.Multiply, "") }),
        new("Placement & mining range +", "Extra tiles of reach for everything (vanilla is +0).", false, false,
            new[] { ("*", "tileBoost", RuleOp.Add, "") }),
        new("Auto-swing every weapon", "Hold the mouse button to keep attacking.", true, false,
            new[] { ("weapon", "autoReuse", RuleOp.Set, "true") }),
        new("Infinite ammo", "Arrows, bullets, darts etc. are never used up.", true, false,
            new[] { ("ammo", "consumable", RuleOp.Set, "false") }),
        new("Infinite consumables", "Potions, food, thrown weapons AND placeable blocks are never used up.", true, false,
            new[] { ("consumable", "consumable", RuleOp.Set, "false") }),
    };

    static readonly Preset[] NpcPresets =
    {
        new("Enemy health ×", "All hostile NPCs, bosses included.", false, true, new[] { ("hostile", "lifeMax", RuleOp.Multiply, "") }),
        new("Enemy damage ×", "Contact damage of hostile NPCs (projectiles are unaffected).", false, true, new[] { ("hostile", "damage", RuleOp.Multiply, "") }),
        new("Enemy defense ×", "", false, true, new[] { ("hostile", "defense", RuleOp.Multiply, "") }),
        new("Enemy coin drops ×", "Money dropped by enemies.", false, true, new[] { ("hostile", "value", RuleOp.Multiply, "") }),
        new("Boss health ×", "Stacks with Enemy health.", false, true, new[] { ("boss", "lifeMax", RuleOp.Multiply, "") }),
        new("Boss damage ×", "Stacks with Enemy damage.", false, true, new[] { ("boss", "damage", RuleOp.Multiply, "") }),
    };

    static readonly (string key, string label, string hint)[] Cooldowns =
    {
        ("Terraria.Item.potionDelay", "Potion sickness (healing potions)", ""),
        ("Terraria.Item.restorationDelay", "Restoration potion sickness", ""),
        ("Terraria.Item.mushroomDelay", "Mushroom sickness", ""),
    };

    public TweaksView() => InitializeComponent();

    public void OnShown()
    {
        if (AppState.RulesChangedOnDisk) AppState.ReloadRules();
        Build();
        NotPatchedHint.Visibility = AppState.GameDir is { } d && Patcher.GetStatus(Path.Combine(d, "Terraria.exe")).State == PatchState.Vanilla
            ? Visibility.Visible : Visibility.Collapsed;
    }

    void Build()
    {
        var rules = AppState.Rules;
        SpawnPanel.Children.Clear();
        var sd = AppState.Game?.StaticDefaults;
        if (sd != null && sd.TryGetValue("Terraria.NPC.defaultSpawnRate", out int exeRate) && sd.TryGetValue("Terraria.NPC.defaultMaxSpawns", out int exeMax)
            && (exeRate != 600 || exeMax != 5))
            SpawnPanel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Foreground = (System.Windows.Media.Brush)FindResource("Warn"),
                Text = $"Your Terraria.exe was already modified: it spawns {600.0 / exeRate:0.#}× faster and allows {exeMax / 5.0:0.#}× more enemies than vanilla. " +
                       "While patched, the values below replace that - 1× is true vanilla."
            });
        SpawnPanel.Children.Add(SliderRow("Spawn rate", "How often enemies spawn. 2× = twice as often, 0.25× = rarely. (Battle / Water Candle effects still stack on top.)",
            rules.SpawnRate, 0.05, 20, v => { AppState.Rules.SpawnRate = v; AppState.RulesEdited(); }));
        SpawnPanel.Children.Add(SliderRow("Max enemies", "How many enemies can be around you at once.",
            rules.MaxSpawns, 0.1, 20, v => { AppState.Rules.MaxSpawns = v; AppState.RulesEdited(); }));

        BuildDisplay();
        BuildSummons();

        CooldownPanel.Children.Clear();
        foreach (var (key, label, _) in Cooldowns) CooldownPanel.Children.Add(CooldownRow(key, label));
        CooldownPanel.Children.Add(DialRow("Enchanted Sundial cooldown", "Terraria.Main.Sundialing", "dawns",
            () => AppState.Rules.SundialDays, v => AppState.Rules.SundialDays = v));
        CooldownPanel.Children.Add(DialRow("Enchanted Moondial cooldown", "Terraria.Main.Moondialing", "dusks",
            () => AppState.Rules.MoondialDays, v => AppState.Rules.MoondialDays = v));
        CooldownPanel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 6, 0, 0),
            Text = "Per-item use time and reuse delay are in the Item Editor."
        });

        ItemPanel.Children.Clear();
        foreach (var p in ItemPresets) ItemPanel.Children.Add(PresetRow(p));
        NpcPanel.Children.Clear();
        foreach (var p in NpcPresets) NpcPanel.Children.Add(PresetRow(p));
    }

    void BuildSummons()
    {
        SummonPanel.Children.Clear();
        SummonPanel.Children.Add(CountRow("Extra minion slots",
            "Added to your base of 1; armor, accessories and buffs still add their own. 5 extra = a 6-minion base. Each minion still uses the slots it costs.",
            0, 0, 200, () => AppState.Rules.MinionBonus, v => AppState.Rules.MinionBonus = v, "extra"));
        SummonPanel.Children.Add(CountRow("Pets at once",
            "Vanilla keeps one pet: summoning a new one removes the old. With a higher number, new pets are added next to the old ones; the oldest go once you pass the limit.",
            1, 1, 40, () => AppState.Rules.MaxPets, v => AppState.Rules.MaxPets = v, "pets"));
        SummonPanel.Children.Add(CountRow("Light pets at once",
            "Same for light pets (Shadow Orb, Wisp, Fairy...). Their light adds up.",
            1, 1, 40, () => AppState.Rules.MaxLightPets, v => AppState.Rules.MaxLightPets = v, "light pets"));
        SummonPanel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 8, 0, 0),
            Text = "Extra pets are summoned the normal way (use or equip the pet item, one after another). They are kept as buffs, so they stay with your character " +
                   "between sessions, and you can also add them in the Character Editor under Buffs. The pet, light pet, minecart, mount and hook equipment slots themselves " +
                   "stay as they are: the game's save format has exactly five, so adding more could corrupt characters. " +
                   "Minion and pet changes only matter on the machine of the player who owns them."
        });
    }

    /// <summary>A whole-number setting where <paramref name="vanilla"/> means "unchanged" (shown as the placeholder).</summary>
    FrameworkElement CountRow(string label, string hint, int vanilla, int min, int max, Func<int> get, Action<int> set, string unit)
    {
        var grid = Row(label, hint + $" Vanilla: {vanilla}.");
        var box = new TextBox { Width = 80, Text = get().ToString(CultureInfo.InvariantCulture) };
        void Commit()
        {
            if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) set(Math.Clamp(v, min, max));
            else set(vanilla);
            box.Text = get().ToString(CultureInfo.InvariantCulture);
            AppState.RulesEdited(this);
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Commit(); };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Foreground = (System.Windows.Media.Brush)FindResource("Muted") });
        Grid.SetColumn(panel, 1);
        grid.Children.Add(panel);
        return grid;
    }

    const string WideScreenKey = "Terraria.Main.SupportWideScreen";

    /// <summary>
    /// Terraria forces a minimum zoom of max(1, width/1920, height/1200), so above 1080p the view is just
    /// magnified and you never see more world. Its own (hidden) SupportWideScreen switch removes that.
    /// </summary>
    void BuildDisplay()
    {
        DisplayPanel.Children.Clear();
        bool on = AppState.Rules.Statics.TryGetValue(WideScreenKey, out var v) && v.Equals("true", StringComparison.OrdinalIgnoreCase);
        var res = GameConfig.ReadResolution(AppState.SaveDir);
        string size = res is { } r ? $"{r.width}×{r.height}" : "your resolution";
        double zoom = res is { } z ? Math.Max(1.0, Math.Max(z.width / 1920.0, z.height / 1200.0)) : 1;

        var cb = new CheckBox
        {
            IsChecked = on, Margin = new Thickness(0, 0, 0, 4),
            Content = new TextBlock { Text = "Unlock the resolution / zoom cap (use my full native resolution)", FontWeight = FontWeights.SemiBold }
        };
        cb.Click += (_, _) =>
        {
            if (cb.IsChecked == true)
            {
                AppState.Rules.Statics[WideScreenKey] = "true";
                if (!GameLocator.IsGameRunning() && GameConfig.EnableSupport4K(AppState.SaveDir))
                    MainWindow.Instance?.SetStatus("Also switched on Terraria's own Support4K option in config.json (backup: config.json.tcbak).");
            }
            else AppState.Rules.Statics.Remove(WideScreenKey);
            AppState.RulesEdited(this);
            BuildDisplay();
        };
        DisplayPanel.Children.Add(cb);
        DisplayPanel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Hint"), Margin = new Thickness(22, 0, 0, 6),
            Text = zoom > 1.0
                ? $"Vanilla Terraria never shows more than a 1920×1200 area: at {size} it forces a {zoom:0.##}× minimum zoom, so everything is just magnified. " +
                  $"Unlocked, the game renders at the full {size} and the zoom slider starts at 1× (native)."
                : "Vanilla Terraria never shows more than a 1920×1200 area and magnifies the view on bigger screens. Unlocked, the game renders at your full resolution."
        });
        DisplayPanel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Hint"), Margin = new Thickness(22, 0, 0, 0), Foreground = (System.Windows.Media.Brush)FindResource("Warn"),
            Text = "Restart Terraria for the full effect. Up to 4096 px per side. Rain, falling stars and enemy-spawn areas still use the vanilla 1920×1200 region, so the outer edge of a very wide view can look emptier."
        });

        if (GameConfig.ReadSupport4K(AppState.SaveDir) == false)
            DisplayPanel.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("Hint"), Margin = new Thickness(22, 8, 0, 0), Foreground = (System.Windows.Media.Brush)FindResource("Bad"),
                Text = "Terraria's own \"Support4K\" option is off in config.json. It will be switched on when you tick this box (game closed)."
            });
    }

    // ---------------------------------------------------------------- rows

    FrameworkElement SliderRow(string label, string hint, double value, double min, double max, Action<double> set)
    {
        var grid = Row(label, hint);
        // log scale so 0.1x .. 20x is usable
        var slider = new Slider { Minimum = Math.Log(min), Maximum = Math.Log(max), Value = Math.Log(value), Width = 260, VerticalAlignment = VerticalAlignment.Center };
        var box = new TextBox { Width = 64, Text = RulesDocument.F(value), Margin = new Thickness(10, 0, 0, 0) };
        var reset = new Button { Content = "1×", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 3, 8, 3) };
        bool sync = false;
        slider.ValueChanged += (_, _) =>
        {
            if (sync) return;
            double v = Math.Round(Math.Exp(slider.Value), Math.Exp(slider.Value) < 1 ? 2 : 1);
            sync = true; box.Text = RulesDocument.F(v); sync = false;
            set(v);
        };
        void Commit()
        {
            if (sync || !double.TryParse(box.Text.Trim().TrimEnd('x', '×'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v <= 0) return;
            v = Math.Clamp(v, min, max);
            sync = true; slider.Value = Math.Log(v); sync = false;
            set(v);
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Commit(); };
        reset.Click += (_, _) => { box.Text = "1"; Commit(); };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(slider); panel.Children.Add(box); panel.Children.Add(new TextBlock { Text = "×", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) }); panel.Children.Add(reset);
        Grid.SetColumn(panel, 1);
        grid.Children.Add(panel);
        return grid;
    }

    FrameworkElement CooldownRow(string key, string label)
    {
        int vanillaTicks = AppState.Game?.StaticDefaults.GetValueOrDefault(key) ?? 0;
        string hint = vanillaTicks > 0 ? $"Vanilla: {vanillaTicks / 60.0:0.#} seconds. Leave empty for vanilla." : "Leave empty for vanilla.";
        var grid = Row(label, hint);
        var box = new TextBox { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
        if (AppState.Rules.Statics.TryGetValue(key, out var ticks) && double.TryParse(ticks, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
            box.Text = (t / 60).ToString("0.##", CultureInfo.InvariantCulture);
        void Commit()
        {
            var text = box.Text.Trim();
            if (text.Length == 0) AppState.Rules.Statics.Remove(key);
            else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs) && secs >= 0)
                AppState.Rules.Statics[key] = ((int)Math.Round(secs * 60)).ToString(CultureInfo.InvariantCulture);
            else { box.Text = ""; return; }
            AppState.RulesEdited(this);
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Commit(); };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock { Text = "seconds", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Foreground = (System.Windows.Media.Brush)FindResource("Muted") });
        Grid.SetColumn(panel, 1);
        grid.Children.Add(panel);
        return grid;
    }

    /// <summary>Sundial / Moondial: cooldown in days, counted down at each dawn (sundial) or dusk (moondial).</summary>
    FrameworkElement DialRow(string label, string defaultsKey, string unit, Func<int?> get, Action<int?> set)
    {
        int vanilla = AppState.Game?.StaticDefaults.GetValueOrDefault(defaultsKey) ?? 0;
        string hint = (vanilla > 0 ? $"Vanilla: {vanilla} days. " : "") + $"Counts down at each {unit[..^1]}. 0 = use it every day. Leave empty for vanilla.";
        var grid = Row(label, hint);
        var box = new TextBox { Width = 80, Text = get()?.ToString(CultureInfo.InvariantCulture) ?? "" };
        void Commit()
        {
            var text = box.Text.Trim();
            if (text.Length == 0) set(null);
            else if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int days) && days is >= 0 and <= 1000) set(days);
            else { box.Text = get()?.ToString(CultureInfo.InvariantCulture) ?? ""; return; }
            AppState.RulesEdited(this);
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Commit(); };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock { Text = "days", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Foreground = (System.Windows.Media.Brush)FindResource("Muted") });
        Grid.SetColumn(panel, 1);
        grid.Children.Add(panel);
        return grid;
    }

    FrameworkElement PresetRow(Preset p)
    {
        var list = p.IsNpc ? AppState.Rules.Npcs : AppState.Rules.Items;
        var grid = Row(p.Label, p.Hint);
        FrameworkElement editor;
        if (p.IsToggle)
        {
            var cb = new CheckBox { IsChecked = p.Rules.All(r => AppState.Rules.Get(list, r.sel, r.field)?.Value == r.toggleValue), VerticalAlignment = VerticalAlignment.Center };
            cb.Click += (_, _) =>
            {
                foreach (var r in p.Rules)
                    AppState.Rules.Set(list, r.sel, r.field, cb.IsChecked == true ? new FieldRule(r.sel, r.field, r.op, r.toggleValue) : null);
                AppState.RulesEdited(this);
            };
            editor = cb;
        }
        else
        {
            var existing = AppState.Rules.Get(list, p.Rules[0].sel, p.Rules[0].field);
            var box = new TextBox { Width = 80, Text = existing?.Op == p.Rules[0].op ? existing.Value : "" };
            void Commit()
            {
                var text = box.Text.Trim().TrimStart('x', '×', '+');
                bool off = text.Length == 0 || (p.Rules[0].op == RuleOp.Multiply && text == "1") || (p.Rules[0].op == RuleOp.Add && text == "0");
                if (!off && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) { box.Text = ""; off = true; }
                foreach (var r in p.Rules)
                    AppState.Rules.Set(list, r.sel, r.field, off ? null : new FieldRule(r.sel, r.field, r.op, text));
                if (off) box.Text = "";
                AppState.RulesEdited(this);
            }
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Commit(); };
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(box);
            panel.Children.Add(new TextBlock
            {
                Text = p.Rules[0].op == RuleOp.Add ? "tiles   (empty = vanilla)" : "×   (empty = vanilla)",
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Foreground = (System.Windows.Media.Brush)FindResource("Muted")
            });
            editor = panel;
        }
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    Grid Row(string label, string hint)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var sp = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        sp.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });
        if (hint.Length > 0) sp.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("Hint") });
        grid.Children.Add(sp);
        return grid;
    }

    void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Reset spawn rates, cooldowns and all group presets? (Per-item and per-NPC edits are kept.)", "Reset world rules",
                            MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var r = AppState.Rules;
        r.SpawnRate = r.MaxSpawns = 1;
        foreach (var (key, _, _) in Cooldowns) r.Statics.Remove(key);
        r.SundialDays = r.MoondialDays = null;
        r.Statics.Remove(WideScreenKey);
        r.MinionBonus = 0; r.MaxPets = r.MaxLightPets = 1;
        foreach (var p in ItemPresets) foreach (var x in p.Rules) r.Set(r.Items, x.sel, x.field, null);
        foreach (var p in NpcPresets) foreach (var x in p.Rules) r.Set(r.Npcs, x.sel, x.field, null);
        AppState.RulesEdited(this);
        Build();
    }
}
