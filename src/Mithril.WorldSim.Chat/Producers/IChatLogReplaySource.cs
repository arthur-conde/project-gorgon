using Mithril.Shared.Logging;

namespace Mithril.WorldSim.Chat.Producers;

/// <summary>
/// Chat-side analog of <see cref="IClassifiedPlayerLogStream"/> — the
/// session-replay-aware source that <see cref="ChatLogProducer"/> wraps to
/// feed the chat world. Yields raw chat lines wrapped in
/// <see cref="LogEnvelope{T}"/> so the structural <c>IsReplay</c> bit is
/// sourced authoritatively from the source's own session-replay-vs-live
/// boundary, matching the Player-side L1 envelope contract.
///
/// <para><b>Why this exists separately from <see cref="IChatLogStream"/>.</b>
/// The legacy <see cref="IChatLogStream"/> is live-only by design — it
/// <c>SeedDirectoryToCurrentEnd</c>s on attach so long-running chat files
/// don't flood new sessions. Principle 9 of the world simulator design
/// notebook explicitly replaces that choice for world-sim consumers: the
/// chat world must drain from the PG-session-start chat banner, symmetric
/// with Player.log's session replay, so views that fuse the two streams can
/// claim determinism over their joined source set. The world's replay-aware
/// source therefore lives at the world-sim layer (this assembly), keeping the
/// legacy live-only stream available for its existing consumers (Saruman,
/// Smaug, etc.) without behaviour drift.</para>
/// </summary>
public interface IChatLogReplaySource
{
    /// <summary>
    /// Subscribe to the chat session's lines. On attach, seeks to the most
    /// recent chat banner (principle 9) and yields lines from there forward
    /// with <see cref="LogEnvelope{T}.IsReplay"/> set to <c>true</c>. Once
    /// the replay backlog is drained — every existing line from the banner
    /// onward has been yielded — the source transitions to live-tail and
    /// yields subsequent appended lines with <c>IsReplay = false</c>. The
    /// flag flips exactly once and never re-arms.
    ///
    /// <para>Sources with no replay phase (no banner found, empty
    /// directory, …) yield directly in live mode — the first envelope
    /// carries <c>IsReplay = false</c>. Sources whose replay phase exhausts
    /// without ever transitioning to live (the underlying source stream
    /// closes mid-replay) complete the enumeration without ever yielding
    /// a live envelope; consumers handle this via the producer's
    /// <c>ReachedLive</c> completion contract (degenerate live-only path).</para>
    /// </summary>
    IAsyncEnumerable<LogEnvelope<RawLogLine>> SubscribeWithReplayMarkerAsync(CancellationToken ct);
}
