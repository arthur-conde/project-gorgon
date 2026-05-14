namespace Silmarillion.ViewModels;

/// <summary>
/// Display projection of one <see cref="Mithril.Reference.Models.Npcs.NpcPreference"/> for the
/// NPCs tab detail pane. <see cref="Desire"/> is the raw sentiment string (Love / Like /
/// Dislike / Hate) — matches Arwen's vocabulary. <see cref="Pref"/> is the multiplier on
/// the gift baseline (positive for Love/Like, negative for Dislike/Hate).
/// </summary>
public sealed record NpcPreferenceRow(
    string Desire,
    string DisplayName,
    double Pref,
    IReadOnlyList<string> Keywords,
    string? MinFavorTier);
