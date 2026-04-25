using Mithril.Shared.Reference;

namespace Mithril.Shared.Wpf;

/// <summary>
/// Bundle of pre-parsed previews surfaced as collapsible sections on
/// <see cref="ItemDetailWindow"/>. All members are nullable; an unset (or empty) list
/// hides the corresponding section. Lets callers add new preview shapes without
/// growing the <see cref="IItemDetailPresenter"/> overload surface.
/// </summary>
public sealed record ItemDetailContext(
    IReadOnlyList<AugmentPreview>? Augments = null,
    IReadOnlyList<WaxItemPreview>? WaxItems = null,
    IReadOnlyList<WaxAugmentPreview>? WaxAugments = null,
    IReadOnlyList<AugmentPoolPreview>? AugmentPools = null,
    IReadOnlyList<TaughtRecipePreview>? TaughtRecipes = null,
    IReadOnlyList<EffectTagPreview>? EffectTags = null)
{
    public static ItemDetailContext Empty { get; } = new();
}
