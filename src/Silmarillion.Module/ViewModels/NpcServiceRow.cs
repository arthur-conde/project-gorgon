using Mithril.Shared.Wpf;

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
    IReadOnlyList<NpcServiceDetailLine> Details);

/// <summary>
/// One line in a service row's detail strip. <see cref="Text"/> carries the prose ("Despised → 5,000g",
/// "Skills: Toolcrafting, Non-Fiction Writing") and is always rendered. <see cref="Chips"/> is an
/// optional trailing chip strip — Store cap-increase rows surface their per-tier keyword tuple as
/// navigable <see cref="EntityChipVm"/>s targeting the Items tab via <see cref="Mithril.Shared.Reference.EntityKind.ItemKeyword"/>.
/// Empty for non-Store rows; the XAML hides the strip when the list is empty.
/// </summary>
public sealed record NpcServiceDetailLine(string Text, IReadOnlyList<EntityChipVm> Chips)
{
    public static NpcServiceDetailLine TextOnly(string text) => new(text, []);
}
