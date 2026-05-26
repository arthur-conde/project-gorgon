using System.Windows;
using System.Windows.Threading;
using Arda.Dispatch;
using Arda.World.Player.Events;

using Arwen.Domain;
using Mithril.Shared.Character;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Settings;
using Microsoft.Extensions.Hosting;
using ArdaGiftAccepted = Arda.World.Player.Events.GiftAccepted;

namespace Arwen.State;

/// <summary>
/// Eager Arwen ingestion. Two channels, both subscribed via
/// <see cref="IDomainEventSubscriber"/>:
/// <list type="bullet">
///   <item><b><see cref="InteractionStarted"/></b> — drives the per-character
///   exact-favor snapshot (parsed from <c>ProcessStartInteraction</c> by the
///   Arda Npc handler).</item>
///   <item><b><see cref="ArdaGiftAccepted"/></b> — emitted by the Npc handler's
///   internal FSM which correlates the <c>ProcessStartInteraction</c> /
///   <c>ProcessDeleteItem</c> / <c>ProcessDeltaFavor</c> verb triple at L3
///   dispatch. Does not carry <c>ItemInternalName</c> — name resolution is
///   deferred to the inventory ledger (retained-entry lookups).</item>
/// </list>
/// </summary>
public sealed class FavorIngestionService : BackgroundService
{
    private readonly FavorStateService _state;
    private readonly CalibrationService _calibration;
    private readonly PerCharacterView<ArwenFavorState> _favorView;
    private readonly IDiagnosticsSink? _diag;
    private readonly IDisposable? _interactionSub;
    private readonly IDisposable? _giftSub;
    private Dispatcher? _dispatcher;

    public FavorIngestionService(
        IDomainEventSubscriber bus,
        FavorStateService state,
        CalibrationService calibration,
        PerCharacterView<ArwenFavorState> favorView,
        IActiveCharacterService activeChar,
        SettingsAutoSaver<ArwenSettings> autoSaver,
        IDiagnosticsSink? diag = null)
    {
        _state = state;
        _calibration = calibration;
        _favorView = favorView;
        _diag = diag;
        _ = activeChar; // keep alive for character-change subscription
        _ = autoSaver;  // keep alive for PropertyChanged subscription

        _interactionSub = bus.Subscribe<InteractionStarted>(OnInteractionStarted);
        _giftSub = bus.Subscribe<ArdaGiftAccepted>(OnArdaGiftAccepted);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _diag?.Info("Arwen.Ingestion",
            "Subscribing to Arda domain bus (InteractionStarted + GiftAccepted) for favor tracking and calibration");

        _dispatcher = Application.Current?.Dispatcher;

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on host stop */ }
    }

    private void OnInteractionStarted(InteractionStarted e)
    {
        if (!e.IsNpc || string.IsNullOrEmpty(e.Name)) return;

        var ts = e.Metadata.Timestamp ?? e.Metadata.ReadOn;

        void Apply()
        {
            _diag?.Trace("Arwen.Parse", $"FavorUpdate npc={e.Name} favor={e.Favor:F1}");
            var favor = _favorView.Current;
            if (favor is not null)
            {
                favor.SetExactFavor(e.Name, e.Favor, ts);
                _favorView.Save();
                _state.OnFavorUpdated(e.Name);
            }
        }

        if (_dispatcher is null || _dispatcher.CheckAccess())
            Apply();
        else
            _dispatcher.InvokeAsync(Apply);
    }

    private void OnArdaGiftAccepted(ArdaGiftAccepted gift)
    {
        var ts = gift.Metadata.Timestamp ?? gift.Metadata.ReadOn;

        void Dispatch() => _calibration.OnGiftAccepted(
            gift.NpcKey, gift.ItemInstanceId, gift.DeltaFavor, ts);

        if (_dispatcher is null || _dispatcher.CheckAccess())
            Dispatch();
        else
            _dispatcher.InvokeAsync(Dispatch);
    }

    public override void Dispose()
    {
        _interactionSub?.Dispose();
        _giftSub?.Dispose();
        base.Dispose();
    }
}
