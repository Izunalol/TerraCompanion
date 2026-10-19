using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TerraCompanion.Core.Rules;

namespace TerraCompanion.App.Views;

/// <summary>Direct editor for rules.txt, for anything the other pages don't expose.</summary>
public sealed class RawRulesView : UserControl, IPage
{
    readonly TextBox _editor = new()
    {
        AcceptsReturn = true, AcceptsTab = true, FontFamily = new FontFamily("Consolas"), FontSize = 13,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalContentAlignment = VerticalAlignment.Top, VerticalAlignment = VerticalAlignment.Stretch, TextWrapping = TextWrapping.NoWrap
    };
    readonly TextBlock _state = new();

    const string Help =
@"spawnRate = 2                        enemy spawn-rate multiplier
maxSpawns = 1.5                      max-enemies multiplier
minionBonus = 5                      extra minion slots (vanilla 0)
maxPets = 3                          pets out at once (vanilla 1)
maxLightPets = 2                     light pets out at once (vanilla 1)
petSlots = 0                         hide the extra pet equipment slots (default: shown)
extraAccessories = 4                  extra accessory slots (0-14), each with a vanity and a dye slot
sundialCooldown = 2                  Enchanted Sundial cooldown in days (vanilla 8)
moondialCooldown = 0                 Enchanted Moondial cooldown in days (0 = every night)
static Terraria.Item.potionDelay = 1800   any static field, by full type name

item <who> <field> = 300            set
item <who> <field> *= 1.5           multiply
item <who> <field> += 10            add
  <who> for items: an ID, *, stackable, consumable, weapon, ammo, accessory, tool
npc <who> lifeMax *= 2
  <who> for NPCs: an ID (negative IDs too), *, boss, hostile, friendly, town

<field> is any public number / true-false field on Terraria.Item or Terraria.NPC,
e.g. damage, useTime, useAnimation, reuseDelay, crit, knockBack, mana, consumable,
maxStack, autoReuse, pick, axe, hammer, defense, value, rare, lifeMax, knockBackResist.
Group rules run first, then ID rules - so an ID rule always wins.
Mistakes are listed in the hook log (Game & Patch page) and never crash the game.";

    public RawRulesView()
    {
        var root = new DockPanel();
        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        top.Children.Add(new TextBlock { Text = "Raw Rules", Style = (Style)Application.Current.FindResource("H1") });
        top.Children.Add(new TextBlock { Text = "This is the exact file the patched game reads. The other pages edit it for you; here you can write anything.", Style = (Style)Application.Current.FindResource("Hint"), FontSize = 13 });
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var save = new Button { Content = "Save", Style = (Style)Application.Current.FindResource("Primary") };
        var reload = new Button { Content = "Reload from disk" };
        var open = new Button { Content = "Open folder" };
        save.Click += (_, _) => Save();
        reload.Click += (_, _) => Load();
        open.Click += (_, _) => { Directory.CreateDirectory(AppState.CompanionDir); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppState.CompanionDir) { UseShellExecute = true }); };
        buttons.Children.Add(save); buttons.Children.Add(reload); buttons.Children.Add(open);
        _state.VerticalAlignment = VerticalAlignment.Center;
        _state.Margin = new Thickness(10, 0, 0, 0);
        _state.Foreground = (Brush)Application.Current.FindResource("Muted");
        buttons.Children.Add(_state);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var help = new Border
        {
            Style = (Style)Application.Current.FindResource("Card"), Margin = new Thickness(16, 0, 0, 0), Width = 470,
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock { Text = Help, FontFamily = new FontFamily("Consolas"), FontSize = 12, Foreground = (Brush)Application.Current.FindResource("Muted") }
            }
        };
        DockPanel.SetDock(help, Dock.Right);
        root.Children.Add(help);
        root.Children.Add(_editor);
        Content = root;
        _editor.TextChanged += (_, _) => _state.Text = "unsaved changes";
    }

    public void OnShown()
    {
        AppState.Flush();
        Load();
    }

    void Load()
    {
        AppState.ReloadRules();
        _editor.Text = File.Exists(AppState.RulesPath) ? File.ReadAllText(AppState.RulesPath) : AppState.Rules.ToText();
        _state.Text = File.Exists(AppState.RulesPath) ? AppState.RulesPath : "not saved yet";
    }

    void Save()
    {
        Directory.CreateDirectory(AppState.CompanionDir);
        File.WriteAllText(AppState.RulesPath, _editor.Text);
        AppState.ReloadRules();
        _state.Text = $"saved {DateTime.Now:T} - the game reloads it automatically";
    }
}
