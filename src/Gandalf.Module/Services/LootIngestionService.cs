using Gandalf.Parsing;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Microsoft.Extensions.Hosting;

namespace Gandalf.Services;

/// <summary>
/// Subscribes to <see cref="IPlayerLogStream"/> and routes loot-related events
/// into <see cref="LootSource"/>. No <c>ModuleGate</c> wait — Gandalf is eager;
/// derived-source log replay must run as soon as the host starts.
///
/// Chest interaction + rejection are correlated: the rejection screen text
/// doesn't carry the chest name, but per the wiki it only fires inside an
/// interaction bracket, so the most-recent chest interaction is the one being
/// rejected. We track the last chest name in-process and pair on rejection.
/// </summary>
public sealed class LootIngestionService : BackgroundService
{
    private readonly IPlayerLogStream _stream;
    private readonly ChestInteractionParser _chestInteraction;
    private readonly ChestRejectionParser _chestRejection;
    private readonly DefeatRewardParser _defeatReward;
    private readonly LootSource _source;
    private readonly IDiagnosticsSink? _diag;
    private string? _lastChestInternalName;
    private bool _firstObservationLogged;

    public LootIngestionService(
        IPlayerLogStream stream,
        ChestInteractionParser chestInteraction,
        ChestRejectionParser chestRejection,
        DefeatRewardParser defeatReward,
        LootSource source,
        IDiagnosticsSink? diag = null)
    {
        _stream = stream;
        _chestInteraction = chestInteraction;
        _chestRejection = chestRejection;
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
        if (_chestInteraction.TryParse(raw.Line, raw.Timestamp) is ChestInteractionEvent chest)
        {
            _lastChestInternalName = chest.ChestInternalName;
            _source.OnChestInteraction(chest.ChestInternalName, chest.Timestamp);
            FirstObservation();
            return;
        }

        if (_chestRejection.TryParse(raw.Line, raw.Timestamp) is ChestCooldownObservedEvent rejection)
        {
            // Rejection screen text doesn't carry the chest name — pair it to the
            // most recent chest interaction we observed inside this bracket.
            if (string.IsNullOrEmpty(_lastChestInternalName))
            {
                _diag?.Warn("Gandalf.Loot", "ChestRejection without preceding interaction — skipped");
                return;
            }
            _source.OnChestCooldownObserved(_lastChestInternalName, rejection.Duration);
            return;
        }

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
