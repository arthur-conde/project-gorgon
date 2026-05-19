namespace Mithril.Shared.Logging;

/// <summary>
/// Abstract base for typed parser events (L2 territory). Every parser
/// (<see cref="ILogParser"/> / <see cref="IChatLogParser"/>) returns a
/// concrete subclass — <c>WeatherChangedEvent</c>, <c>QuestAcceptedEvent</c>,
/// <c>MapPinLogEvent</c>, etc. <c>Timestamp</c> is the <b>UTC</b> instant
/// the event occurred — carried as <see cref="DateTime"/> (Kind=Utc)
/// because parsers accept <c>(string line, DateTime timestamp)</c> per the
/// standing "convert at the boundary" convention. The L0
/// <see cref="RawLogLine.Timestamp"/> is a TZ-correct
/// <see cref="DateTimeOffset"/> (UTC for Player.log lines, host-local for
/// chat lines); consumers fold it to UTC via <c>.UtcDateTime</c> at the
/// parser invocation site, so the resulting <see cref="LogEvent"/> always
/// holds the same UTC instant regardless of source — not the local
/// wall-clock for chat-derived events.
///
/// <para><b>Not related to <see cref="RawLogLine"/></b> beyond a shared
/// historical accident. <see cref="RawLogLine"/> is L0 (tailed-from-disk,
/// pre-parse); <see cref="LogEvent"/> subclasses are L2 (typed,
/// recognised). They no longer share an inheritance chain — collapsing
/// them was an artifact of an earlier design and conflated two
/// concerns.</para>
/// </summary>
public abstract record LogEvent(DateTime Timestamp);

/// <summary>
/// One physical line read from a tailed log source. L0 of the layered
/// log pipeline (#511 / #513): emitted by <c>LogSourceTailer</c>,
/// consumed by every state-rebuilder and reactive ingestion service via
/// <see cref="IPlayerLogStream"/> / <see cref="IChatLogStream"/>.
///
/// <para><b><see cref="Timestamp"/></b> — the absolute wall-clock instant
/// the line represents, normalized to a <see cref="DateTimeOffset"/>
/// regardless of source. For Player.log this is the <c>[HH:MM:SS]</c>
/// prefix folded over the session anchor's UTC date (offset
/// <see cref="TimeSpan.Zero"/>). For chat logs this is the <c>yy-MM-dd
/// HH:mm:ss</c> prefix interpreted as host-local time and carried as a
/// <see cref="DateTimeOffset"/> with the host's local UTC offset for that
/// instant. Downstream of L0 nothing ever again needs to know which
/// source the line came from to compute a TZ-correct instant — this
/// structurally eliminates the #183 bug class (no per-consumer
/// <c>new DateTimeOffset(ts, TimeSpan.Zero)</c> can apply the wrong
/// offset to a chat-derived line because there is no per-consumer
/// conversion left to get wrong).</para>
///
/// <para><b><see cref="Sequence"/></b> — per-source monotonic counter.
/// Derived from the line's byte offset within the current physical source
/// file: each emitted line starts at a strictly greater offset than the
/// previous. Restart-stable: a mid-session Mithril restart re-seeds at
/// some byte offset N and resumes emitting <see cref="Sequence"/> values
/// consistent with N, so an L1 consumer's persisted high-water key
/// continues to filter correctly (#511 deliverable 3). The point of
/// deriving from log position rather than from a process counter is
/// exactly this restart-stability — see #513 scope addition A.
/// <em>Caveat: on detected rotation/truncation (<c>fs.Length &lt; _offset</c>)
/// the sequence space resets to 0 alongside the file offset</em> — the prior
/// physical file's sequence space ends; consumers must observe rotations
/// distinctly (the L1 high-water filter scopes its key to the same
/// physical-source generation). See <c>LogSourceTailer</c> for the
/// implementing contract.</para>
///
/// <para><b><see cref="ReadMonotonicTicks"/></b> — the high-resolution
/// monotonic instant <see cref="TimeProvider.GetTimestamp"/> reported
/// when L0 tailed the batch containing this line. <i>Distinct</i> from
/// <see cref="Timestamp"/>: Timestamp is the (second-resolution,
/// game-supplied) "when this event happened in-game"; ReadMonotonicTicks
/// is "when Mithril read it off disk". For <b>live</b> lines from two
/// sources (Player.log + chat) tailed by the same Mithril process,
/// ReadMonotonicTicks is a usable fine-grained tiebreaker within a
/// shared game-second — the Tier-3 mechanism in the #523 correlation
/// hierarchy. For <b>replay</b> lines or while tailing falls behind
/// (the alarmed #507 condition) it is meaningless and consumers must
/// fall back to per-source <see cref="Sequence"/> + game-second + keyed
/// correlation. L0 mints the value; L1 (#511 deliverable 3) will add
/// the <c>IsReplay</c> flag that tells the consumer when the value is
/// usable.</para>
///
/// <para><see cref="Sequence"/> and <see cref="ReadMonotonicTicks"/>
/// default to <c>0</c> so test code that constructs synthetic
/// <see cref="RawLogLine"/> instances via <c>new RawLogLine(ts, "line")</c>
/// continues to compile unchanged; production L0 always populates both.</para>
/// </summary>
public sealed record RawLogLine(
    DateTimeOffset Timestamp,
    string Line,
    long Sequence = 0,
    long ReadMonotonicTicks = 0);
