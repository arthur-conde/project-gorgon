using System.Windows;
using System.Windows.Threading;
using Arda.Dispatch;
using Arda.World.Player;
using Arda.World.Player.Events;
using Mithril.Shared.Diagnostics;
using Microsoft.Extensions.Hosting;
using Smaug.Domain;

namespace Smaug.State;

/// <summary>
/// Subscribes to Arda domain events via <see cref="IDomainEventSubscriber"/>
/// for vendor-related activity and feeds recorded sells into
/// <see cref="PriceCalibrationService"/>.
///
/// <para><b>Arda migration.</b> Replaces the former L1
/// <c>ILogStreamDriver</c> + <c>VendorLogParser</c> path. Subscriptions
/// are wired in the constructor so they are in place before the Arda
/// drivers start pumping. The <see cref="IDomainEventSubscriber"/> fires
/// synchronously on the driver thread.</para>
///
/// <para><b>Civic Pride via IPlayerState.</b> Instead of subscribing to
/// <c>IPlayerSkillState</c> snapshots, reads <see cref="IPlayerState.Skills"/>
/// on demand at sell-record time. The Arda Player handler keeps the state
/// current through replay and live updates.</para>
///
/// <para><b>Threading.</b> The Arda bus fires synchronously on the driver
/// thread. In WPF contexts we marshal onto the dispatcher so state mutations
/// (and the downstream <c>PriceCalibrationService.DataChanged</c> →
/// <c>CalibrationViewModel.Refresh()</c> → <c>ObservableCollection</c>
/// mutation path) stay on the UI thread. In test/headless contexts where
/// <see cref="Application.Current"/> is null we run inline.</para>
/// </summary>
public sealed class VendorIngestionService : BackgroundService
{
    private readonly PriceCalibrationService _calibration;
    private readonly VendorSellContext _context;
    private readonly IPlayerState _playerState;
    private readonly IDiagnosticsSink? _diag;
    private readonly IDisposable _interactionSub;
    private readonly IDisposable _vendorScreenSub;
    private readonly IDisposable _itemSoldSub;
    private readonly IDisposable _goldUpdatedSub;
    private Dispatcher? _dispatcher;

    public VendorIngestionService(
        IDomainEventSubscriber bus,
        PriceCalibrationService calibration,
        VendorSellContext context,
        IPlayerState playerState,
        IDiagnosticsSink? diag = null)
    {
        _calibration = calibration;
        _context = context;
        _playerState = playerState;
        _diag = diag;

        _interactionSub = bus.Subscribe<InteractionStarted>(OnInteractionStarted);
        _vendorScreenSub = bus.Subscribe<VendorScreenOpened>(OnVendorScreenOpened);
        _itemSoldSub = bus.Subscribe<VendorItemSold>(OnVendorItemSold);
        _goldUpdatedSub = bus.Subscribe<VendorGoldUpdated>(OnVendorGoldUpdated);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _diag?.Info("Smaug",
            "Subscribing to Arda domain bus for vendor events (InteractionStarted, VendorScreenOpened, VendorItemSold, VendorGoldUpdated)");

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

        void Apply() => _context.RememberEntity((int)e.EntityId, e.Name);

        if (_dispatcher is null || _dispatcher.CheckAccess())
            Apply();
        else
            _dispatcher.InvokeAsync(Apply);
    }

    private void OnVendorScreenOpened(VendorScreenOpened e)
    {
        void Apply()
        {
            _context.OnVendorScreenOpened(e.EntityId, e.FavorTier);
            _diag?.Trace("Smaug.Parse",
                $"VendorScreen entity={e.EntityId} npc={_context.ActiveNpcKey ?? "?"} tier={e.FavorTier}");
        }

        if (_dispatcher is null || _dispatcher.CheckAccess())
            Apply();
        else
            _dispatcher.InvokeAsync(Apply);
    }

    private void OnVendorItemSold(VendorItemSold e)
    {
        void Apply()
        {
            if (!_context.IsReadyToRecord)
            {
                _diag?.Trace("Smaug.Parse",
                    $"Sell of {e.InternalName} for {e.Price} skipped — no active vendor context");
                return;
            }

            var civicPride = 0;
            if (_playerState.Skills.TryGetValue("CivicPride", out var cp))
                civicPride = cp.Raw;

            _calibration.RecordObservation(
                _context.ActiveNpcKey!,
                e.InternalName,
                e.Price,
                _context.ActiveFavorTier!,
                civicPride,
                e.Metadata.Timestamp ?? e.Metadata.ReadOn);
        }

        if (_dispatcher is null || _dispatcher.CheckAccess())
            Apply();
        else
            _dispatcher.InvokeAsync(Apply);
    }

    private void OnVendorGoldUpdated(VendorGoldUpdated e)
    {
        _diag?.Trace("Smaug.Parse",
            $"VendorGold remaining={e.RemainingGold} cap={e.GoldCap}");
    }

    public override void Dispose()
    {
        _interactionSub.Dispose();
        _vendorScreenSub.Dispose();
        _itemSoldSub.Dispose();
        _goldUpdatedSub.Dispose();
        base.Dispose();
    }
}
