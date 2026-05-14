using PocoEffect = Mithril.Reference.Models.Effects.Effect;

namespace Silmarillion.ViewModels;

/// <summary>
/// Master-list row for the Silmarillion Effects tab. Wraps the <see cref="PocoEffect"/>
/// POCO and surfaces the fields the query box and card template need (with collection
/// fields surfaced via <see cref="EffectKeywordValue"/> wrappers so <c>CONTAINS</c>
/// queries work). <see cref="EnvelopeKey"/> is the selection identity — names collide
/// across effects (multiple <c>"Riposte!"</c> entries exist), so the envelope key is
/// what selection-preservation captures across refreshes.
/// </summary>
public sealed record EffectListRow(
    PocoEffect Effect,
    string EnvelopeKey,
    string DisplayName,
    int IconId,
    string? StackingType,
    string? Duration,
    IReadOnlyList<EffectKeywordValue> Keywords);
