using System.Windows;
using System.Windows.Threading;
using Arda.Composition;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Mithril.Shared.Diagnostics;
using Microsoft.Extensions.Hosting;
using Smaug.Domain;

namespace Smaug.State;

/// <summary>
/// Subscribes to Arda domain events via <see cref="IDomainEventSubscriber"/>
/// for vendor sell activity and feeds recorded sells into
/// <see cref="PriceCalibrationService"/>.
///
/// <para><b>Vendor context.</b> The Arda <c>Npc</c> handler enriches
/// <see cref="VendorItemSold"/> with the resolved NPC key and favor tier
/// from the active vendor session, so this service no longer maintains its
/// own entity-to-NPC mapping or vendor session state.</para>
///
/// <para><b>Civic Pride via IPlayerProgressionState.</b> Reads the persisted
/// Civic Pride level at sell-record time (available at cold start).</para>
/// </summary>
public sealed class VendorIngestionService : BackgroundService
{
    private readonly PriceCalibrationService _calibration;
    private readonly IPlayerProgressionState _progression;
    private readonly IDiagnosticsSink? _diag;
    private readonly IDisposable _itemSoldSub;
    private Dispatcher? _dispatcher;

    public VendorIngestionService(
        IDomainEventSubscriber bus,
        PriceCalibrationService calibration,
        IPlayerProgressionState progression,
        IDiagnosticsSink? diag = null)
    {
        _calibration = calibration;
        _progression = progression;
        _diag = diag;

        _itemSoldSub = bus.Subscribe<VendorItemSold>(OnVendorItemSold);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _diag?.Info("Smaug",
            "Subscribing to Arda domain bus for vendor events (VendorItemSold)");

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

            var civicPride = _progression.Skills.TryGetValue("CivicPride", out var cp) ? cp.Level : 0;

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

    public override void Dispose()
    {
        _itemSoldSub.Dispose();
        base.Dispose();
    }
}
