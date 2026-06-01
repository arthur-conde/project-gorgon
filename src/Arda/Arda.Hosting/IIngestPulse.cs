using Arda.Abstractions.Diagnostics;

namespace Arda.Hosting;

/// <summary>
/// Read-side of the tailer-poll pulse signal. Reports when each log-family
/// tailer last completed a poll iteration of its live-tail loop, distinct
/// from when the game last wrote a log line. Drives "is the tailer
/// running?" liveness in <see cref="Arda.Contracts.State.Health.WorldHealth"/>.
/// <para>
/// Read-side lives in Arda.Hosting because the singleton implementation is
/// composed here. The write-side (<see cref="IIngestPulseSink"/>) lives in
/// Arda.Abstractions so Arda.Ingest can record pulses without taking a
/// dependency on Arda.Hosting. The same singleton implements both sides.
/// </para>
/// </summary>
public interface IIngestPulse
{
    /// <summary>
    /// Wall-clock time of the most recent poll for the family, or null if
    /// that family hasn't polled yet (e.g. still resolving session start,
    /// no chat file present on disk yet).
    /// </summary>
    DateTimeOffset? LastPoll(LogFamily family);

    /// <summary>
    /// Fires after every poll iteration for any family. Consumers that derive
    /// "stalled" state should recompute it inside this handler — pulses from
    /// either family are the natural revaluation cadence.
    /// </summary>
    event EventHandler<IngestPulseEventArgs>? Pulsed;
}

/// <summary>
/// Payload for <see cref="IIngestPulse.Pulsed"/>. Intentionally a struct so
/// the publisher doesn't allocate per poll on hot ingest paths.
/// </summary>
public readonly record struct IngestPulseEventArgs(
    LogFamily Family,
    DateTimeOffset PolledAt,
    int LinesEmitted);
