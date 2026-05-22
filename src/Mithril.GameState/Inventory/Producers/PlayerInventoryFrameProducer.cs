using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.WorldSim;

namespace Mithril.GameState.Inventory.Producers;

/// <summary>
/// World-simulator producer that adapts the L1 driver's
/// <see cref="ILogStreamDriver"/> push-based <c>LocalPlayer</c> subscription
/// into the world's pull-based <see cref="IFrameProducer{TPayload}"/> contract
/// for the Player.log inventory folder (#602 — Phase 2 of the world-sim
/// migration). Parses every classified LocalPlayer line via the inventory-
/// relevant regexes; only <c>ProcessAddItem</c> / <c>ProcessDeleteItem</c> /
/// <c>ProcessUpdateItemCode</c> / <c>ProcessRemoveFromStorageVault</c> yield
/// frame emissions — every other line is dropped at this boundary so the world
/// sees only inventory frames.
///
/// <para><b>Mode awareness.</b> Mirrors
/// <see cref="Mithril.GameState.Skills.Producers.SkillFrameProducer"/>'s shape
/// — <see cref="ReachedLive"/> completes the moment the producer reads the
/// first non-replay envelope from the L1 driver. Inventory verbs can be sparse
/// during idle play; we mustn't stall the world's mode flip waiting for one.</para>
///
/// <para><b>L1 subscription disposition.</b> Archetype-A defaults
/// (<see cref="ReplayMode.FromSessionStart"/> + <see cref="DeliveryContext.Inline"/>),
/// matching the pre-migration <c>InventoryService</c> exactly — the L0.5
/// router strips the <c>LocalPlayer:</c> envelope, the parser consumes
/// <see cref="LocalPlayerLogLine.Data"/> directly, and L1 owns containment
/// around each handler invocation.</para>
/// </summary>
public sealed partial class PlayerInventoryFrameProducer
    : IFrameProducer<PlayerInventoryFrame>, IModeAwareFrameProducer<PlayerInventoryFrame>
{
    // ProcessAddItem(InternalName(instanceId), slot, bool)
    [GeneratedRegex(@"ProcessAddItem\((\w+)\((\d+)\),", RegexOptions.CultureInvariant)]
    private static partial Regex AddItemRx();

    // ProcessDeleteItem(instanceId)
    [GeneratedRegex(@"ProcessDeleteItem\((\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex DeleteItemRx();

    // ProcessUpdateItemCode(instanceId, code, bool)
    [GeneratedRegex(@"ProcessUpdateItemCode\((\d+),\s*(\d+),\s*\w+\)", RegexOptions.CultureInvariant)]
    private static partial Regex UpdateItemCodeRx();

    // ProcessRemoveFromStorageVault(arg1, arg2, instanceId, stackSize) — args 1/2 are vault metadata.
    [GeneratedRegex(@"ProcessRemoveFromStorageVault\([^,]+,\s*[^,]+,\s*(\d+),\s*(\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex RemoveFromStorageVaultRx();

    private readonly ILogStreamDriver _driver;
    private readonly IDiagnosticsSink? _diag;
    private readonly TaskCompletionSource _reachedLive = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PlayerInventoryFrameProducer(
        ILogStreamDriver driver,
        IDiagnosticsSink? diag = null)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _diag = diag;
    }

    /// <summary>
    /// Producer priority for the merger's tie-breaking. Matches the
    /// classified-pipe producer's priority (0) — same source, same ordering
    /// rights.
    /// </summary>
    public int Priority => 0;

    public Task ReachedLive => _reachedLive.Task;

    public async IAsyncEnumerable<Frame<PlayerInventoryFrame>> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<Frame<PlayerInventoryFrame>>(
            new UnboundedChannelOptions { SingleReader = true });

        _diag?.Info("GameState.Inventory.Player",
            "PlayerInventoryFrameProducer subscribing to L1 driver (LocalPlayer pipe) for inventory frames");

        var subscription = _driver.Subscribe<LocalPlayerLogLine>(
            envelope =>
            {
                if (!envelope.IsReplay)
                {
                    _reachedLive.TrySetResult();
                }

                var line = envelope.Payload;
                var payload = TryParse(line.Data);
                if (payload is null) return ValueTask.CompletedTask;

                _ = channel.Writer.TryWrite(new Frame<PlayerInventoryFrame>(line.Timestamp, payload));
                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions
            {
                ReplayMode = ReplayMode.FromSessionStart,
                DeliveryContext = DeliveryContext.Inline,
                DiagnosticCategory = "GameState.Inventory.Player",
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

    /// <summary>
    /// Parse one classified <c>LocalPlayer:</c> line body into an inventory
    /// frame payload, or return <c>null</c> if the line is not inventory-
    /// relevant. Internal for direct unit testing — the producer's only other
    /// behaviour is L1 envelope shape, which the existing
    /// <c>SkillFrameProducer</c> end-to-end pattern covers.
    /// </summary>
    internal static PlayerInventoryFrame? TryParse(string data)
    {
        var add = AddItemRx().Match(data);
        if (add.Success && long.TryParse(add.Groups[2].ValueSpan, out var addId))
        {
            return new PlayerInventoryAddFrame(addId, add.Groups[1].Value);
        }

        var del = DeleteItemRx().Match(data);
        if (del.Success && long.TryParse(del.Groups[1].ValueSpan, out var delId))
        {
            return new PlayerInventoryRemoveFrame(delId);
        }

        var upd = UpdateItemCodeRx().Match(data);
        if (upd.Success
            && long.TryParse(upd.Groups[1].ValueSpan, out var updId)
            && long.TryParse(upd.Groups[2].ValueSpan, out var code))
        {
            return new PlayerInventoryUpdateItemCodeFrame(updId, code);
        }

        var vault = RemoveFromStorageVaultRx().Match(data);
        if (vault.Success
            && long.TryParse(vault.Groups[1].ValueSpan, out var vaultId)
            && int.TryParse(vault.Groups[2].ValueSpan, out var vaultSize))
        {
            return new PlayerInventoryVaultWithdrawFrame(vaultId, vaultSize);
        }

        return null;
    }
}
