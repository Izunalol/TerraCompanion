using System.Globalization;
using TerraCompanion.Core.Rules;

namespace TerraCompanion.App.Views;

public sealed class ItemsView : RuleEditorView
{
    const string Ticks = "Measured in ticks: 60 ticks = 1 second. Lower is faster.";

    static readonly FieldDef[] Defs =
    {
        new("damage", "Damage", "Combat", FieldKind.Int),
        new("crit", "Crit chance bonus (%)", "Combat", FieldKind.Int, "Added to the base 4% critical strike chance."),
        new("knockBack", "Knockback", "Combat", FieldKind.Float),
        new("useTime", "Use time", "Combat", FieldKind.Int, Ticks + " How often the item fires/acts."),
        new("useAnimation", "Use animation", "Combat", FieldKind.Int, Ticks + " Length of one swing. Usually change this together with use time."),
        new("reuseDelay", "Cooldown after use", "Combat", FieldKind.Int, Ticks + " Extra wait before the item can be used again."),
        new("autoReuse", "Auto-swing", "Combat", FieldKind.Bool),
        new("mana", "Mana cost", "Combat", FieldKind.Int),
        new("shoot", "Projectile ID", "Combat", FieldKind.Int, "Which projectile the item fires (0 = none)."),
        new("shootSpeed", "Projectile speed", "Combat", FieldKind.Float),
        new("armorPenetration", "Armor penetration", "Combat", FieldKind.Int),
        new("scale", "Size", "Combat", FieldKind.Float, "Visual and hitbox scale of swung weapons."),

        new("consumable", "Consumed on use", "Consumption & stacking", FieldKind.Bool, "Set to No for an infinite potion, block, ammo or thrown weapon."),
        new("maxStack", "Max stack", "Consumption & stacking", FieldKind.Int),
        new("potion", "Causes potion sickness", "Consumption & stacking", FieldKind.Bool),
        new("healLife", "Heals life", "Consumption & stacking", FieldKind.Int),
        new("healMana", "Restores mana", "Consumption & stacking", FieldKind.Int),
        new("buffType", "Buff ID given", "Consumption & stacking", FieldKind.Int, "Buff applied when used (0 = none)."),
        new("buffTime", "Buff duration", "Consumption & stacking", FieldKind.Int, "In ticks (60 = 1 second)."),

        new("pick", "Pickaxe power (%)", "Tools", FieldKind.Int),
        new("axe", "Axe power", "Tools", FieldKind.Int, "Shown in game as this value x5 %."),
        new("hammer", "Hammer power (%)", "Tools", FieldKind.Int),
        new("tileBoost", "Range bonus (tiles)", "Tools", FieldKind.Int),

        new("defense", "Defense", "Equipment", FieldKind.Int),
        new("lifeRegen", "Life regeneration", "Equipment", FieldKind.Int),
        new("manaIncrease", "Max mana bonus", "Equipment", FieldKind.Int),

        new("value", "Value (copper coins)", "Value", FieldKind.Int, "Buy price in copper. 100 copper = 1 silver, 10000 = 1 gold. Sells for 1/5."),
        new("rare", "Rarity", "Value", FieldKind.Int, "-1 grey, 0 white, 1 blue, 2 green ... 10 red, 11 purple, -12 expert rainbow, -13 master."),
    };

    protected override string Noun => "item";
    protected override FieldDef[] Fields => Defs;
    protected override List<FieldRule> RuleList => AppState.Rules.Items;
    protected override DefaultsTable? Defaults => AppState.ItemDefaults;
    protected override string[] Categories => new[] { "Weapons", "Tools", "Accessories", "Armor", "Consumables", "Ammo" };

    protected override IEnumerable<EntityEntry> LoadEntries()
    {
        var g = AppState.Game;
        if (g == null) yield break;
        for (int id = 1; id < g.ItemCount; id++)
            yield return new EntityEntry { Id = id, Name = g.ItemName(id), Sub = $"#{id}  {g.ItemInternalNames[id]}", HasIcon = true };
    }

    protected override bool InCategory(int id, string category)
    {
        var d = Defaults;
        if (d == null) return true;
        return category switch
        {
            "Weapons" => d.Num(id, "damage") > 0 && d.Num(id, "ammo") == 0 && !d.Flag(id, "accessory") && !d.Flag(id, "consumable"),
            "Tools" => d.Num(id, "pick") > 0 || d.Num(id, "axe") > 0 || d.Num(id, "hammer") > 0,
            "Accessories" => d.Flag(id, "accessory"),
            "Armor" => d.Num(id, "defense") > 0 && !d.Flag(id, "accessory"),
            "Consumables" => d.Flag(id, "consumable") && d.Num(id, "ammo") == 0,
            "Ammo" => d.Num(id, "ammo") > 0,
            _ => true
        };
    }

    protected override string DescribeValue(string field, string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return "";
        return field switch
        {
            "useTime" or "useAnimation" or "reuseDelay" or "buffTime" when v > 0 => $"({v / 60:0.##} s)",
            "buffType" when v > 0 && AppState.Game != null => $"({AppState.Game.BuffName((int)v)})",
            "value" when v > 0 => $"({Coins((long)v)})",
            _ => ""
        };
    }

    static string Coins(long c)
    {
        var parts = new List<string>();
        if (c >= 1_000_000) { parts.Add($"{c / 1_000_000}p"); c %= 1_000_000; }
        if (c >= 10_000) { parts.Add($"{c / 10_000}g"); c %= 10_000; }
        if (c >= 100) { parts.Add($"{c / 100}s"); c %= 100; }
        if (c > 0) parts.Add($"{c}c");
        return string.Join(" ", parts);
    }
}
