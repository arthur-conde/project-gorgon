namespace Mithril.Shared.Reference;

/// <summary>
/// Parsed projection of an <c>AdjustRecipeReuseTime(deltaSeconds, condition)</c>
/// entry in <see cref="RecipeEntry.ResultEffects"/>. The crafted item or recipe
/// shifts the cooldown of *another* recipe by <see cref="DeltaSeconds"/>,
/// optionally only under <see cref="Condition"/> (e.g. <c>QuarterMoon</c>).
/// <para>
/// <see cref="DisplayText"/> is pre-formatted at parse time —
/// e.g. <c>"Reduces cooldown by 1d on Quarter Moon"</c> — so the chip
/// renders the same way regardless of who consumes it.
/// </para>
/// </summary>
public sealed record RecipeCooldownPreview(
    int DeltaSeconds,
    string? Condition,
    string DisplayText);
