using Mithril.Shared.Storage;

namespace Mithril.Shared.Wpf;

/// <summary>
/// Module-agnostic input record consumed by <see cref="IIngredientSourcesPresenter"/>.
/// Modules (Celebrimbor today, Bilbo / Smaug later) project their domain rows into
/// this shape and call <c>Show</c>; the window itself doesn't depend on any module.
/// </summary>
/// <param name="Title">Display heading, e.g. <c>"Auxiliary Crystal"</c> or the item's name.</param>
/// <param name="KeywordsLabel">When set, e.g. <c>"any Crystal"</c>, the row represents a keyword-matched slot rather than a single item.</param>
/// <param name="ItemInternalName">Single-item rows only; null for keyword rows. Drives the Sources lookup.</param>
/// <param name="OnHand">Per-(item, location) slices already collected by the caller.</param>
public sealed record IngredientSourcesInput(
    string Title,
    string? KeywordsLabel,
    string? ItemInternalName,
    IReadOnlyList<IngredientLocation> OnHand);
