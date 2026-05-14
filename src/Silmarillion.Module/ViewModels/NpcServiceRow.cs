namespace Silmarillion.ViewModels;

/// <summary>
/// Display projection of one <see cref="Mithril.Reference.Models.Npcs.NpcService"/> for the
/// NPCs tab detail pane. <see cref="Details"/> is the flattened, human-readable list of
/// per-subclass sub-rows (CapIncreases for stores, Skills/Unlocks for trainers, etc.) — the
/// detail view renders one bullet per entry under the service header. <see cref="MinFavorTier"/>
/// surfaces the access threshold (Favor on the raw POCO) as its own chip.
/// </summary>
public sealed record NpcServiceRow(
    string Type,
    string? MinFavorTier,
    IReadOnlyList<string> Details);
