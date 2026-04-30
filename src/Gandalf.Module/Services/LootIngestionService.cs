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
/// naming heuristic. Defeat parsing runs as a parallel path because kill
/// credits don't share the interaction-bracket envelope.
///
/// No <c>ModuleGate</c> wait — Gandalf is eager; derived-source log replay
/// must run as soon as the host starts.
/// </summary>
public sealed class LootIngestionService : BackgroundService
{
    private readonly IPlayerLogStream _stream;
    private readonly LootBracketTracker _bracket;
    private readonly DefeatRewardParser _defeatReward;
    private readonly LootSource _source;
    private readonly IDiagnosticsSink? _diag;
    private bool _firstObservationLogged;

    public LootIngestionService(
        IPlayerLogStream stream,
        LootBracketTracker bracket,
        DefeatRewardParser defeatReward,
        LootSource source,
        IDiagnosticsSink? diag = null)
    {
        _stream = stream;
        _bracket = bracket;
        _defeatReward = defeatReward;
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

        if (_defeatReward.TryParse(raw.Line, raw.Timestamp) is DefeatRewardEvent defeat)
        {
            _source.OnDefeatReward(defeat.NpcDisplayName, defeat.Timestamp);
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
