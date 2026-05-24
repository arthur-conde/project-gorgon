using Gandalf.Parsing;
using Microsoft.Extensions.Hosting;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;

namespace Gandalf.Services;

/// <summary>
/// Post-#550 PR 3 L1 migration. Subscribes to the L1
/// <see cref="ILogStreamDriver"/>'s LocalPlayer pipe (the typed L0.5
/// actor-classified surface) and routes loot-related events into
/// <see cref="LootSource"/>. Chest detection delegates to
/// <see cref="LootBracketTracker"/> — a signal-driven state machine that
/// distinguishes loot chests from storage vaults / NPC dialog without a
/// naming heuristic. Boss kills route through <see cref="BossKillCreditParser"/>
/// — Combat Wisdom is awarded only on defeat-cooldown creature kills, so the
/// wisdom-credit line is both the auto-discovery signal AND the cooldown
/// anchor (the <see cref="DefeatCooldownParser"/> rejection text remains as
/// a diagnostic-only "cooldown still active" observation).
///
/// <para><b>Area→chest-stamp bridge (#790).</b> Owned by <see cref="LootSource"/>
/// directly — it injects <c>IPlayerAreaState</c> and queries
/// <c>CurrentArea</c> at chest-commit time. This service no longer has any
/// area dep; the per-line area-tracker push-in retired in #790 because
/// the producer's L1 subscription already owns the <c>LOADING LEVEL</c>
/// envelope path end-to-end.</para>
///
/// <para><b>L1 migration disposition (#549 Gandalf row, #550 PR 3 archetype-B).</b>
/// <list type="bullet">
///   <item><see cref="ReplayMode.FromSessionStart"/> — eager module; the
///   chest/defeat catalog must rebuild from session-start because the
///   per-key upserts in <see cref="LootSource"/> need the full backlog to
///   re-derive <c>LearnedChest</c> / <c>LearnedDefeat</c> rows on every cold
///   start.</item>
///   <item><see cref="DeliveryContext.Inline"/> — the handler writes to
///   dictionaries + raises events; no <c>ObservableCollection</c> mutation
///   in the ingestion path. VM-side dispatcher marshalling lives in
///   <c>DashboardAggregator</c> / <c>TimerSourceBinder</c>, untouched by
///   this migration.</item>
///   <item><b>No</b> <see cref="LogSubscriptionOptions.SkipProcessedHighWater"/>
///   — idempotent upsert: <see cref="LootSource.OnChestInteraction"/> /
///   <see cref="LootSource.OnBossKillCredit"/> short-circuit on the
///   per-key <c>(chest:&lt;internal&gt;|defeat:&lt;displayName&gt;)</c>
///   <c>StartedAt</c> equality guard, so re-replay of the same Sequence
///   re-applies a no-op without inflating any counter. The #549 audit row
///   explicitly declines an L1 high-water for this consumer.</item>
/// </list>
/// </para>
///
/// <para><b>Containment retired.</b> The L1 driver wraps every handler
/// invocation in try/catch + rate-limited Warn (#550 capability C), so the
/// pre-L1 hand-rolled <c>_diag?.Warn("Gandalf.Loot", ...)</c> catch this
/// service used to hold is gone. Failures surface on
/// <see cref="IDiagnosticsSink"/> under the <c>Gandalf.Loot</c> category via
/// the driver's <see cref="LogSubscriptionOptions.DiagnosticCategory"/>
/// override (preserves the pre-L1 bucket — log consumers see no category
/// churn).</para>
///
/// <para>No <c>ModuleGate</c> wait — Gandalf is eager; derived-source log
/// replay must run as soon as the host starts.</para>
/// </summary>
public sealed class LootIngestionService : BackgroundService
{
    private const string DiagCategory = "Gandalf.Loot";

    private readonly ILogStreamDriver _driver;
    private readonly LootBracketTracker _bracket;
    private readonly BossKillCreditParser _bossKill;
    private readonly DefeatCooldownParser _defeatCooldown;
    private readonly LootSource _source;
    private readonly IDiagnosticsSink? _diag;
    private ILogSubscription? _subscription;
    private bool _firstObservationLogged;

    public LootIngestionService(
        ILogStreamDriver driver,
        LootBracketTracker bracket,
        BossKillCreditParser bossKill,
        DefeatCooldownParser defeatCooldown,
        LootSource source,
        IDiagnosticsSink? diag = null)
    {
        _driver = driver;
        _bracket = bracket;
        _bossKill = bossKill;
        _defeatCooldown = defeatCooldown;
        _source = source;
        _diag = diag;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _diag?.Info(DiagCategory, "Subscribing to L1 driver (LocalPlayer pipe) for loot ingestion");

        _subscription = _driver.Subscribe<LocalPlayerLogLine>(
            envelope =>
            {
                Dispatch(envelope.Payload);
                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions
            {
                ReplayMode = ReplayMode.FromSessionStart,
                DeliveryContext = DeliveryContext.Inline,
                DiagnosticCategory = DiagCategory,
            });

        // Park until the host stops. The L1 subscription runs its own pump
        // on a Task.Run; ExecuteAsync's job is to dispose it on shutdown.
        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on host stop */ }
        finally
        {
            _subscription?.Dispose();
            _subscription = null;
        }
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        base.Dispose();
    }

    private void Dispatch(LocalPlayerLogLine payload)
    {
        // L0.5 (#532) eats the [ts] + LocalPlayer: envelope; the parsers
        // and bracket tracker consume LocalPlayerLogLine.Data directly
        // (anchor dropped per the #550 PR #555 review — "downstream never
        // re-matches the actor envelope").
        var line = payload.Data;
        var ts = payload.Timestamp.UtcDateTime;

        _bracket.Observe(line, ts);

        if (_bossKill.TryParse(line, ts) is BossKillCreditEvent kill)
        {
            _source.OnBossKillCredit(kill.NpcDisplayName, kill.Timestamp);
            FirstObservation();
            return;
        }

        if (_defeatCooldown.TryParse(line, ts) is DefeatCooldownActiveEvent active)
        {
            _source.OnDefeatCooldownActive(active.NpcDisplayName, active.Timestamp);
            FirstObservation();
        }
    }

    private void FirstObservation()
    {
        if (_firstObservationLogged) return;
        _firstObservationLogged = true;
        _diag?.Info(DiagCategory, "First loot-source event observed this session");
    }
}
