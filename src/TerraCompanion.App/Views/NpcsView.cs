using TerraCompanion.Core.Rules;

namespace TerraCompanion.App.Views;

public sealed class NpcsView : RuleEditorView
{
    static readonly FieldDef[] Defs =
    {
        new("lifeMax", "Max life", "Stats", FieldKind.Int),
        new("damage", "Contact damage", "Stats", FieldKind.Int, "Damage when touching the player. Projectiles are separate."),
        new("defense", "Defense", "Stats", FieldKind.Int),
        new("knockBackResist", "Knockback taken", "Stats", FieldKind.Float, "1 = full knockback, 0 = immune."),
        new("value", "Coin drop (copper)", "Stats", FieldKind.Float),
        new("scale", "Size", "Stats", FieldKind.Float),
        new("npcSlots", "Spawn slots used", "Stats", FieldKind.Float, "How much of the max-enemies budget this NPC takes."),
        new("noGravity", "Ignores gravity", "Movement", FieldKind.Bool),
        new("noTileCollide", "Passes through blocks", "Movement", FieldKind.Bool),
        new("lavaImmune", "Immune to lava", "Movement", FieldKind.Bool),
    };

    protected override string Noun => "NPC";
    protected override FieldDef[] Fields => Defs;
    protected override List<FieldRule> RuleList => AppState.Rules.Npcs;
    protected override DefaultsTable? Defaults => AppState.NpcDefaults;
    protected override string[] Categories => new[] { "Bosses", "Enemies", "Town NPCs", "Critters & friendly" };

    protected override string ExtraNote =>
        "\nValues are applied after Expert/Master scaling: a set value (\"5000\") is the final number in every difficulty, a multiplier (\"x2\") keeps the difficulty scaling. Vanilla values shown are Classic-mode values.";

    protected override IEnumerable<EntityEntry> LoadEntries()
    {
        var g = AppState.Game;
        if (g == null) yield break;
        foreach (var (id, name) in g.NpcNames)
        {
            if (id == 0) continue;
            g.NpcInternalNames.TryGetValue(id, out var internalName);
            yield return new EntityEntry { Id = id, Name = name, Sub = $"#{id}  {internalName}", HasIcon = false };
        }
    }

    protected override bool InCategory(int id, string category)
    {
        var d = Defaults;
        if (d == null) return true;
        bool boss = d.Flag(id, "boss"), town = d.Flag(id, "townNPC"), friendly = d.Flag(id, "friendly");
        return category switch
        {
            "Bosses" => boss,
            "Enemies" => !friendly && !town && d.Num(id, "lifeMax") > 5,
            "Town NPCs" => town,
            "Critters & friendly" => (friendly && !town) || (!boss && d.Num(id, "lifeMax") <= 5),
            _ => true
        };
    }
}
