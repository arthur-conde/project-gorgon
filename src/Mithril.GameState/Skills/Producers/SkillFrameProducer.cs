using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Mithril.GameState.Skills.Parsing;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.WorldSim;

namespace Mithril.GameState.Skills.Producers;

/// <summary>
/// World-simulator producer that adapts the L1 driver's
/// <see cref="ILogStreamDriver"/> push-based <c>LocalPlayer</c> subscription
/// into the world's pull-based <see cref="IFrameProducer{TPayload}"/> contract
/// for the Player.log skill folder (issue #618 — Phase 1 of the world-sim
/// migration). The producer parses every classified LocalPlayer line via
/// <see cref="SkillLogParser"/>; only <c>ProcessLoadSkills</c> /
/// <c>ProcessUpdateSkill</c> yield <see cref="SkillFrame"/> emissions, every
/// other line is dropped at this boundary so the world sees only skill
/// frames.
///
/// <para><b>Mode awareness.</b> Mirrors
/// <see cref="Mithril.WorldSim.Player.Producers.ClassifiedPlayerLogProducer"/>'s
/// shape — <see cref="ReachedLive"/> completes the moment the producer reads
/// the first non-replay envelope from the L1 driver, irrespective of whether
/// that envelope is itself a skill line. PG's <c>ProcessLoadSkills</c> only
/// fires at login + zone transitions; a player-idle live tail could go
/// minutes without any skill emission, and we mustn't stall the world's mode
/// flip waiting for one.</para>
///
/// <para><b>L1 subscription disposition.</b> Archetype-A defaults
/// (<see cref="ReplayMode.FromSessionStart"/> + <see cref="DeliveryContext.Inline"/>),
/// matching the pre-migration <c>PlayerSkillStateService</c> exactly — the
/// L0.5 router strips the <c>LocalPlayer:</c> envelope, the parser consumes
/// <see cref="LocalPlayerLogLine.Data"/> directly, and L1 owns
/// containment around each handler invocation.</para>
///
/// <para><b>Channel buffer.</b> An unbounded single-reader channel sits
/// between the push-callback and the IAsyncEnumerable yield. The L1 driver
/// already throttles by the source-stream rate (Player.log emit rate is
/// human-real-time bounded), so unbounded is acceptable and avoids the
/// deadlock risk a bounded buffer would introduce (callback would block the
/// L1 pump waiting for the world's merger to drain).</para>
/// </summary>
public sealed class SkillFrameProducer : IFrameProducer<SkillFrame>, IModeAwareFrameProducer<SkillFrame>
{
    private readonly ILogStreamDriver _driver;
    private readonly SkillLogParser _parser;
    private readonly IDiagnosticsSink? _diag;
    private readonly TaskCompletionSource _reachedLive = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SkillFrameProducer(
        ILogStreamDriver driver,
        SkillLogParser parser,
        IDiagnosticsSink? diag = null)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _diag = diag;
    }

    /// <summary>
    /// Producer priority for the merger's tie-breaking. Matches the
    /// classified-pipe producer's priority (0) because the skill producer
    /// also derives from the L1 stream — same source, same ordering rights.
    /// Distinct frame types mean the two producers never share a folder slot,
    /// but they may share timestamps under PG's 1-second resolution; priority
    /// 0 keeps the tie-break stable across runs.
    /// </summary>
    public int Priority => 0;

    public Task ReachedLive => _reachedLive.Task;

    public async IAsyncEnumerable<Frame<SkillFrame>> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        // SingleReader: only the world's merger reads via the async enumerator.
        // Unbounded: see the class-doc rationale (L1 already paces by source).
        var channel = Channel.CreateUnbounded<Frame<SkillFrame>>(
            new UnboundedChannelOptions { SingleReader = true });

        _diag?.Info("GameState.Skills",
            "SkillFrameProducer subscribing to L1 driver (LocalPlayer pipe) for skill-state frames");

        var subscription = _driver.Subscribe<LocalPlayerLogLine>(
            envelope =>
            {
                // Mode flip is driven by envelope-shape, not skill-shape (see
                // class doc). The L1 contract guarantees IsReplay transitions
                // once and never re-arms, so TrySetResult is idempotent past
                // that boundary.
                if (!envelope.IsReplay)
                {
                    _reachedLive.TrySetResult();
                }

                var line = envelope.Payload;
                var evt = _parser.TryParse(line.Data, line.Timestamp.UtcDateTime);
                SkillFrame? payload = evt switch
                {
                    SkillsSnapshotEvent snap => new SkillsSnapshotFrame(snap.Skills),
                    SkillProgressUpdateEvent upd => new SkillProgressUpdateFrame(upd.Skill, upd.XpGained),
                    _ => null,
                };
                if (payload is null) return ValueTask.CompletedTask;

                // TryWrite on an unbounded channel can only fail post-Complete,
                // and we never complete from inside the callback. Discarding
                // the result keeps the hot path branch-free.
                _ = channel.Writer.TryWrite(new Frame<SkillFrame>(line.Timestamp, payload));
                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions
            {
                ReplayMode = ReplayMode.FromSessionStart,
                DeliveryContext = DeliveryContext.Inline,
                DiagnosticCategory = "GameState.Skills",
            });

        try
        {
            await foreach (var frame in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return frame;
            }
        }
        finally
        {
            subscription.Dispose();
            // Guarantee ReachedLive completes even if the stream ended without
            // ever flipping (degenerate test fixture, or a replay-only file
            // tail). Matches ClassifiedPlayerLogProducer's terminal fallback.
            _reachedLive.TrySetResult();
        }
    }
}
