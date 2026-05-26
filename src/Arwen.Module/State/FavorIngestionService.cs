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
using LegacyGiftAccepted = Mithril.GameState.Gifting.GiftAccepted;

namespace Arwen.State;

/// <summary>
/// Eager Arwen ingestion. Two channels, both subscribed via
/// <see cref="IDomainEventSubscriber"/>:
/// <list type="bullet">
///   <item><b><see cref="InteractionStarted"/></b> — drives the per-character
///   exact-favor snapshot (parsed from <c>ProcessStartInteraction</c> by the
///   Arda world driver). Replaces the former L1 LocalPlayer pipe +
///   <c>FavorLogParser</c> path.</item>
///   <item><b><see cref="ArdaGiftAccepted"/></b> — the Arda-layer lift of the
///   gift-detection FSM. The correlator resolves the
///   <c>ProcessStartInteraction</c> / <c>ProcessDeleteItem</c> /
///   <c>ProcessDeltaFavor</c> verb triple and emits a fully-resolved
///   <see cref="ArdaGiftAccepted"/> with the <c>InternalName</c> baked in.
///   Replaces the former <c>IGiftSignalService</c> React-channel
///   subscription.</item>
/// </list>
///
/// <para><b>Subscription timing.</b> Both subscriptions are wired in the
/// constructor so they are in place before the Arda drivers start pumping.
/// The <see cref="IDomainEventSubscriber"/> is synchronous — all events fire inline
/// on the driver thread during <see cref="ExecuteAsync"/>.</para>
///
/// <para><b>Idempotence policy.</b> <em>None</em> from either source's
/// perspective. <see cref="CalibrationService"/> owns per-key dedup at the
/// sink layer via its <c>_observationKeys</c> HashSet keyed
/// <c>SessionId|InstanceId|NpcKey|Item|Delta|Timestamp:O</c>;
/// <see cref="ArwenFavorState.SetExactFavor"/> is an idempotent last-write-wins
/// upsert.</para>
///
/// <para><b>Threading.</b> The Arda bus fires synchronously on the driver
/// thread. In WPF contexts we marshal onto the dispatcher so state mutations
/// stay on the UI thread; in test/headless contexts where
/// <see cref="Application.Current"/> is null we run inline.</para>
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
        var legacyGift = new LegacyGiftAccepted(
            NpcKey: gift.NpcKey,
            ItemInstanceId: gift.ItemInstanceId,
            ItemInternalName: gift.ItemInternalName,
            DeltaFavor: gift.DeltaFavor,
            Timestamp: gift.Metadata.Timestamp ?? gift.Metadata.ReadOn,
            InteractionStartedAt: DateTimeOffset.MinValue);

        void Dispatch() => _calibration.OnGiftAccepted(legacyGift);

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
