using System.Text.Json.Serialization;
using Mithril.Shared.Character;

namespace Legolas.Domain;

/// <summary>
/// Per-character Legolas ingestion bookkeeping — persisted to
/// <c>characters/{slug}/legolas-ingestion.json</c>. Carries the L1
/// high-water <c>Sequence</c> the <c>PlayerLogIngestionService</c> uses to
/// drop already-processed envelopes after a Mithril restart (#550 capability
/// F, the restart-safe replacement for the pre-L1 in-memory
/// <c>_liveSince</c> timestamp guard).
///
/// <para>Per #549's disposition for Legolas/PlayerLog, this is the canonical
/// shape: <c>LiveOnly</c> replay mode plus a persisted Sequence high-water
/// closes both ends of the replay-resurrection bug class — finished-run
/// pins never repopulate either within a single session (LiveOnly drops
/// the replay-phase envelopes) or across a restart (high-water drops live
/// envelopes whose <c>Sequence</c> we've already processed).</para>
///
/// <para>Per-character because the same Mithril install plays multiple PG
/// characters whose Player.log Sequence streams are independent; one global
/// high-water would suppress a freshly-active character's envelopes while
/// their stream's <c>Sequence</c> is still catching up to the prior
/// character's furthest-processed value.</para>
/// </summary>
public sealed class LegolasIngestionState : IVersionedState<LegolasIngestionState>
{
    public const int Version = 1;
    public static int CurrentVersion => Version;
    public static LegolasIngestionState Migrate(LegolasIngestionState loaded) => loaded;

    public int SchemaVersion { get; set; } = Version;

    /// <summary>
    /// Highest <c>LocalPlayerLogLine.Sequence</c> the
    /// <c>PlayerLogIngestionService</c>'s handler has successfully observed
    /// for this character. <c>0</c> on a fresh state — the L1 driver treats
    /// <see cref="LogSubscriptionOptions.SkipProcessedHighWater"/>=<c>0</c>
    /// as "drop only envelopes with <c>Sequence == 0</c>" which is also a
    /// no-op for real lines (the L0 sequencer starts at <c>1</c>). Updated
    /// after each handled envelope and flushed on a short debounce so a
    /// restart resumes from where we left off rather than at the start of
    /// the session-replay window.
    /// </summary>
    public long PlayerLogHighWaterSequence { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(LegolasIngestionState))]
public partial class LegolasIngestionStateJsonContext : JsonSerializerContext { }
