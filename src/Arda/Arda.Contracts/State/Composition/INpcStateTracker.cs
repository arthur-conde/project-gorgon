using Mithril.Reference.Models.Npcs;

namespace Arda.Composition;

/// <summary>
/// A single NPC's last-known state: absolute favor, favor tier, and vendor gold.
/// Per-field timestamps allow consumers to judge recency independently.
/// <para>
/// <see cref="FavorTier"/> uses the canonical <see cref="Mithril.Reference.Models.Npcs.FavorTier"/>
/// enum (#370 convergence). Unknown / never-observed → <see cref="Mithril.Reference.Models.Npcs.FavorTier.Unknown"/>
/// rather than a null sentinel, so consumers compare with typed ordering.
/// </para>
/// </summary>
public readonly record struct NpcRecord(
    string NpcKey,
    double? AbsoluteFavor,
    DateTimeOffset? FavorUpdatedAt,
    FavorTier FavorTier,
    long? RemainingGold,
    long? GoldCap,
    DateTimeOffset? GoldResetsAt,
    DateTimeOffset? GoldUpdatedAt,
    DateTimeOffset LastSeenAt);

/// <summary>
/// Read-only view of per-NPC state accumulated by the L4 <c>NpcStateComposer</c>.
/// Records never expire within a character's lifetime; gold freshness is
/// self-describing via <see cref="NpcRecord.GoldResetsAt"/>.
/// </summary>
public interface INpcStateTracker
{
    /// <summary>All known NPCs keyed by NPC key (e.g. "NPC_Marna").</summary>
    IReadOnlyDictionary<string, NpcRecord> Npcs { get; }

    /// <summary>Look up a single NPC by key. Returns <c>null</c> if never observed.</summary>
    NpcRecord? GetNpc(string npcKey);

    /// <summary>Fires after any mutation (favor update, vendor gold, character switch).</summary>
    event Action? StateChanged;
}
