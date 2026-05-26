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
/// <para><b>Vendor context.</b> The Arda <c>Npc</c> handler enriches
/// <see cref="VendorItemSold"/> with the resolved NPC key and favor tier
/// from the active vendor session, so this service no longer maintains its
/// own entity-to-NPC mapping or vendor session state.</para>
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
    private readonly IPlayerState _playerState;
    private readonly IDiagnosticsSink? _diag;
    private readonly IDisposable _itemSoldSub;
    private readonly IDisposable _goldUpdatedSub;
    private Dispatcher? _dispatcher;

    public VendorIngestionService(
        IDomainEventSubscriber bus,
        PriceCalibrationService calibration,
        IPlayerState playerState,
        IDiagnosticsSink? diag = null)
    {
        _calibration = calibration;
        _playerState = playerState;
        _diag = diag;

        _itemSoldSub = bus.Subscribe<VendorItemSold>(OnVendorItemSold);
        _goldUpdatedSub = bus.Subscribe<VendorGoldUpdated>(OnVendorGoldUpdated);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _diag?.Info("Smaug",
            "Subscribing to Arda domain bus for vendor events (VendorItemSold, VendorGoldUpdated)");

        _dispatcher = Application.Current?.Dispatcher;

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on host stop */ }
    }

    private void OnVendorItemSold(VendorItemSold e)
    {
        void Apply()
        {
            if (string.IsNullOrEmpty(e.NpcKey) || string.IsNullOrEmpty(e.FavorTier))
            {
                _diag?.Trace("Smaug.Parse",
                    $"Sell of {e.InternalName} for {e.Price} skipped — no active vendor context");
                return;
            }

            var civicPride = 0;
            if (_playerState.Skills.TryGetValue("CivicPride", out var cp))
                civicPride = cp.Raw;

            _calibration.RecordObservation(
                e.NpcKey,
                e.InternalName,
                e.Price,
                e.FavorTier,
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
        _itemSoldSub.Dispose();
        _goldUpdatedSub.Dispose();
        base.Dispose();
    }
}
