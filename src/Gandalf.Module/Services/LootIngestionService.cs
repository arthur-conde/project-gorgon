using Gandalf.Parsing;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Microsoft.Extensions.Hosting;

namespace Gandalf.Services;

/// <summary>
/// Subscribes to <see cref="IPlayerLogStream"/> and routes loot-related events
/// into <see cref="LootSource"/>. Chest detection delegates to
/// <see cref="LootBracketTracker"/> — a signal-driven state machine that
/// distinguishes loot chests from storage vaults / NPC dialog without a
/// naming heuristic. Boss kills route through <see cref="BossKillCreditParser"/>
/// — Combat Wisdom is awarded only on defeat-cooldown creature kills, so the
/// wisdom-credit line is both the auto-discovery signal AND the cooldown
/// anchor (the <see cref="DefeatCooldownParser"/> rejection text remains as
/// a diagnostic-only "cooldown still active" observation).
///
/// No <c>ModuleGate</c> wait — Gandalf is eager; derived-source log replay
/// must run as soon as the host starts.
/// </summary>
public sealed class LootIngestionService : BackgroundService
{
    private readonly IPlayerLogStream _stream;
    private readonly LootBracketTracker _bracket;
    private readonly BossKillCreditParser _bossKill;
    private readonly DefeatCooldownParser _defeatCooldown;
    private readonly LootSource _source;
    private readonly IDiagnosticsSink? _diag;
    private bool _firstObservationLogged;

    public LootIngestionService(
        IPlayerLogStream stream,
        LootBracketTracker bracket,
        BossKillCreditParser bossKill,
        DefeatCooldownParser defeatCooldown,
        LootSource source,
        IDiagnosticsSink? diag = null)
    {
        _stream = stream;
        _bracket = bracket;
        _bossKill = bossKill;
        _defeatCooldown = defeatCooldown;
        _source = source;
        _diag = diag;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var raw in _stream.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            try { Dispatch(raw); }
            catch (Exception ex) { _diag?.Warn("Gandalf.Loot", $"Ingestion error: {ex.Message}"); }
        }
    }

    private void Dispatch(RawLogLine raw)
    {
        _bracket.Observe(raw);

        if (_bossKill.TryParse(raw.Line, raw.Timestamp) is BossKillCreditEvent kill)
        {
            _source.OnBossKillCredit(kill.NpcDisplayName, kill.Timestamp);
            FirstObservation();
            return;
        }

        if (_defeatCooldown.TryParse(raw.Line, raw.Timestamp) is DefeatCooldownActiveEvent active)
        {
            _source.OnDefeatCooldownActive(active.NpcDisplayName, active.Timestamp);
            FirstObservation();
        }
    }

    private void FirstObservation()
    {
        if (_firstObservationLogged) return;
        _firstObservationLogged = true;
        _diag?.Info("Gandalf.Loot", "First loot-source event observed this session");
    }
}
