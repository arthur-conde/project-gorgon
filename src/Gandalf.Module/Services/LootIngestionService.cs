using Microsoft.Extensions.Logging;
using Arda.Contracts;
using Arda.World.Player.Events;
using Gandalf.Parsing;
using Microsoft.Extensions.Hosting;
using Mithril.Shared.Diagnostics;

namespace Gandalf.Services;

/// <summary>
/// Arda-native ingestion service for loot events. Subscribes to domain events
/// via <see cref="IDomainEventSubscriber"/> and routes them into
/// <see cref="LootBracketTracker"/> (chest discrimination FSM) and
/// <see cref="LootSource"/> (boss-kill auto-discovery).
///
/// <para>Replaces the legacy L1-driver subscription that fed raw log lines
/// through parser classes. The bracket tracker now receives typed events
/// directly; the parsers for BossKillCredit and DefeatCooldown are retained
/// because those signals arrive as <see cref="ScreenTextObserved"/> and
/// require regex extraction of the NPC display name from free text.</para>
///
/// <para>No <c>ModuleGate</c> wait — Gandalf is eager; derived-source event
/// replay must run as soon as the host starts.</para>
/// </summary>
public sealed class LootIngestionService : BackgroundService
{
    private const string DiagCategory = "Gandalf.Loot";

    private readonly IDomainEventSubscriber _bus;
    private readonly LootBracketTracker _bracket;
    private readonly BossKillCreditParser _bossKill;
    private readonly DefeatCooldownParser _defeatCooldown;
    private readonly LootSource _source;
    private readonly ILogger? _logger;
    private readonly List<IDisposable> _subscriptions = new();
    private bool _firstObservationLogged;

    public LootIngestionService(
        IDomainEventSubscriber bus,
        LootBracketTracker bracket,
        BossKillCreditParser bossKill,
        DefeatCooldownParser defeatCooldown,
        LootSource source,
        ILogger? logger = null)
    {
        _bus = bus;
        _bracket = bracket;
        _bossKill = bossKill;
        _defeatCooldown = defeatCooldown;
        _source = source;
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _logger?.LogDiagnosticInfo(DiagCategory, "Subscribing to Arda domain events for loot ingestion");

        _subscriptions.Add(_bus.Subscribe<InteractionStarted>(evt =>
        {
            _bracket.OnInteractionStarted(evt);
        }));

        _subscriptions.Add(_bus.Subscribe<TalkScreenFrame>(_ =>
        {
            _bracket.OnTalkScreen();
        }));

        _subscriptions.Add(_bus.Subscribe<ScreenTextObserved>(evt =>
        {
            _bracket.OnScreenTextObserved(evt);
            DispatchScreenText(evt);
        }));

        _subscriptions.Add(_bus.Subscribe<EnableInteractorsFrame>(evt =>
        {
            _bracket.OnEnableInteractors(evt);
        }));

        _subscriptions.Add(_bus.Subscribe<InteractionEnded>(evt =>
        {
            _bracket.OnInteractionEnded(evt);
        }));

        _subscriptions.Add(_bus.Subscribe<DelayLoopStarted>(evt =>
        {
            _bracket.OnDelayLoopStarted(evt);
        }));

        _subscriptions.Add(_bus.Subscribe<InteractionWaiting>(evt =>
        {
            _bracket.OnInteractionWaiting(evt);
        }));

        _subscriptions.Add(_bus.Subscribe<InventoryItemAdded>(evt =>
        {
            var ts = evt.Metadata.Timestamp?.UtcDateTime ?? DateTime.UtcNow;
            _bracket.OnInventoryItemAdded(ts);
        }));

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on host stop */ }
        finally { DisposeSubscriptions(); }
    }

    public override void Dispose()
    {
        DisposeSubscriptions();
        base.Dispose();
    }

    private void DisposeSubscriptions()
    {
        foreach (var sub in _subscriptions) sub.Dispose();
        _subscriptions.Clear();
    }

    private void DispatchScreenText(ScreenTextObserved evt)
    {
        var category = evt.Category.ToString();
        var text = evt.Text.ToString();
        var ts = evt.Metadata.Timestamp?.UtcDateTime ?? DateTime.UtcNow;

        // Boss kill credit (CombatInfo channel)
        if (_bossKill.TryParse($"ProcessScreenText({category}, \"{text}\")", ts) is BossKillCreditEvent kill)
        {
            _source.OnBossKillCredit(kill.NpcDisplayName, kill.Timestamp);
            FirstObservation();
            return;
        }

        // Defeat cooldown active (GeneralInfo channel)
        if (_defeatCooldown.TryParse($"ProcessScreenText({category}, \"{text}\")", ts) is DefeatCooldownActiveEvent active)
        {
            _source.OnDefeatCooldownActive(active.NpcDisplayName, active.Timestamp);
            FirstObservation();
        }
    }

    private void FirstObservation()
    {
        if (_firstObservationLogged) return;
        _firstObservationLogged = true;
        _logger?.LogDiagnosticInfo(DiagCategory, "First loot-source event observed this session");
    }
}
