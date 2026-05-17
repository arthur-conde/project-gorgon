namespace Mithril.Shared.Reference;

/// <summary>
/// Typed projection of a tsysclientinfo.json entry — one power that can augment an item,
/// with a dictionary of tier-specific effect descriptions. Looked up by
/// <see cref="InternalName"/> from recipe strings like <c>AddItemTSysPower(InternalName, tier)</c>.
/// </summary>
/// <remarks>
/// <see cref="Suffix"/> / <see cref="Prefix"/> are nullable because not every power
/// carries both display affixes (some have only a <c>Prefix</c>, some only a
/// <c>Suffix</c>, some both). They are an <em>illustrative</em> flavour cue only — the
/// engine-side logic that decides which affixes apply and how they compose onto an item
/// name is not deterministically replicable from CDN data, so they are never a
/// player-facing canonical identity (see <c>pg_tsys_power_naming_ceiling</c> / the #434
/// gate ruling). <see cref="EnvelopeKey"/> is the source <c>power_NNNN</c> key, retained
/// for the Silmarillion G-a footer's storage-only <c>ROW</c> identifier (the catalog is
/// keyed by <see cref="InternalName"/>, so the envelope key is otherwise discarded).
/// </remarks>
public sealed record PowerEntry(
    string InternalName,
    string Skill,
    IReadOnlyList<string> Slots,
    string? Suffix,
    IReadOnlyDictionary<int, PowerTier> Tiers,
    string? Prefix = null,
    string EnvelopeKey = "");
