using Gandalf.Parsing;
using Mithril.GameState.Areas;
using Microsoft.Extensions.Hosting;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Game;
using Mithril.Shared.Logging;

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
/// Also feeds <see cref="PlayerAreaTracker"/> so chest commits stamp the
/// player's current area on <c>LearnedChest.Area</c> (#178). The tracker is
/// reverse-scan-seeded at startup to close the gap where
/// <c>PlayerLogStream.SeedToSessionStart</c> lands ~9 s after the
/// <c>LOADING LEVEL</c> line for the current area.
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
    private readonly PlayerAreaTracker _areaTracker;
    private readonly LootSource _source;
    private readonly GameConfig _config;
    private readonly IDiagnosticsSink? _diag;
    private bool _firstObservationLogged;

    public LootIngestionService(
        IPlayerLogStream stream,
        LootBracketTracker bracket,
        BossKillCreditParser bossKill,
        DefeatCooldownParser defeatCooldown,
        PlayerAreaTracker areaTracker,
        LootSource source,
        GameConfig config,
        IDiagnosticsSink? diag = null)
    {
        _stream = stream;
        _bracket = bracket;
        _bossKill = bossKill;
        _defeatCooldown = defeatCooldown;
        _areaTracker = areaTracker;
        _source = source;
        _config = config;
        _diag = diag;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // One-shot reverse-scan for the most recent LOADING LEVEL Area* line
        // before the live tail kicks in. PlayerLogStream's SessionMarker
        // (ProcessAddPlayer) lands ~9 s after the area-load line, so the
        // live replay window misses it; this seed closes the gap. Best-
        // effort — failures don't block ingestion.
        if (!string.IsNullOrEmpty(_config.PlayerLogPath))
        {
            _areaTracker.SeedFromLog(_config.PlayerLogPath);
        }

        await foreach (var raw in _stream.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            try { Dispatch(raw); }
            catch (Exception ex) { _diag?.Warn("Gandalf.Loot", $"Ingestion error: {ex.Message}"); }
        }
    }

    private void Dispatch(RawLogLine raw)
    {
        // Area transitions update the tracker BEFORE the bracket sees the
        // line, so any subsequent chest interaction in the same dispatch
        // batch reads the correct CurrentArea.
        _areaTracker.Observe(raw);
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
