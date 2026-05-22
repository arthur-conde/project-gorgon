using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Mithril.GameState.WordsOfPower.Parsing;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.WorldSim;

namespace Mithril.GameState.WordsOfPower.Producers;

/// <summary>
/// World-simulator producer that adapts the L1 driver's
/// <see cref="ILogStreamDriver"/> push-based LocalPlayer subscription into the
/// world's pull-based <see cref="IFrameProducer{TPayload}"/> contract for the
/// Player.log Words-of-Power discovery folder (#603). Parses every classified
/// LocalPlayer line via <see cref="WordOfPowerDiscoveredParser"/>; only
/// <c>ProcessBook("You discovered a word of power!", …)</c> lines yield frame
/// emissions.
///
/// <para><b>Mode awareness.</b> <see cref="ReachedLive"/> completes on the
/// first non-replay envelope from the L1 driver, regardless of whether that
/// envelope itself matches the discovery grammar. Discoveries are rare; we
/// mustn't stall the world's mode flip waiting for one.</para>
///
/// <para><b>L1 subscription disposition.</b> Archetype-A defaults
/// (<see cref="ReplayMode.FromSessionStart"/> + <see cref="DeliveryContext.Inline"/>)
/// — the world's merger applies frames deterministically once it receives
/// them, so the producer relays the entire session's discoveries every attach
/// and the folder's idempotent-on-known-code guard suppresses duplicates.
/// The pre-split <see cref="ReplayMode.SinceSubscribe"/> + persisted high-water
/// dedup are no longer needed: the folder's persistent state holds the canonical
/// set across restart, and re-emit of an already-known code is a no-op at the
/// folder.</para>
/// </summary>
public sealed class PlayerWordOfPowerDiscoveryFrameProducer
    : IFrameProducer<WordOfPowerDiscoveryFrame>, IModeAwareFrameProducer<WordOfPowerDiscoveryFrame>
{
    private readonly ILogStreamDriver _driver;
    private readonly IDiagnosticsSink? _diag;
    private readonly TaskCompletionSource _reachedLive = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PlayerWordOfPowerDiscoveryFrameProducer(
        ILogStreamDriver driver,
        IDiagnosticsSink? diag = null)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _diag = diag;
    }

    /// <summary>
    /// Priority 0 — same as the classified-pipe producer; same source, same
    /// ordering rights.
    /// </summary>
    public int Priority => 0;

    public Task ReachedLive => _reachedLive.Task;

    public async IAsyncEnumerable<Frame<WordOfPowerDiscoveryFrame>> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<Frame<WordOfPowerDiscoveryFrame>>(
            new UnboundedChannelOptions { SingleReader = true });

        _diag?.Info("GameState.WordsOfPower.Discovery",
            "PlayerWordOfPowerDiscoveryFrameProducer subscribing to L1 driver (LocalPlayer pipe) for ProcessBook discoveries");

        var subscription = _driver.Subscribe<LocalPlayerLogLine>(
            envelope =>
            {
                if (!envelope.IsReplay)
                {
                    _reachedLive.TrySetResult();
                }

                var line = envelope.Payload;
                var payload = WordOfPowerDiscoveredParser.TryParse(line.Data);
                if (payload is null) return ValueTask.CompletedTask;

                _ = channel.Writer.TryWrite(new Frame<WordOfPowerDiscoveryFrame>(line.Timestamp, payload));
                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions
            {
                ReplayMode = ReplayMode.FromSessionStart,
                DeliveryContext = DeliveryContext.Inline,
                DiagnosticCategory = "GameState.WordsOfPower.Discovery",
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
            _reachedLive.TrySetResult();
        }
    }
}
