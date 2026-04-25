namespace Mithril.Shared.Reference;

/// <summary>
/// Catch-all for low-value zero/one-arg effect prefixes that don't have a richer preview shape:
/// <c>DispelCalligraphyA/B/C</c>, <c>CalligraphyComboNN</c>, and <c>MeditationWithDaily[(combo)]</c>.
/// Renders as a single humanized line in the "Additional effects" section.
/// </summary>
public sealed record EffectTagPreview(string DisplayText);
