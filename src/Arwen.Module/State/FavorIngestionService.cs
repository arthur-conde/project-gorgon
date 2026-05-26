using System.Windows;
using System.Windows.Threading;
using Arda.Dispatch;

using Arwen.Domain;
using Mithril.Shared.Character;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Settings;
using Microsoft.Extensions.Hosting;
using ArdaGiftAccepted = Arda.World.Player.Events.GiftAccepted;

namespace Arwen.State;

/// <summary>
/// Eager Arwen ingestion — subscribes to <see cref="ArdaGiftAccepted"/> via
/// <see cref="IDomainEventSubscriber"/> for gift calibration tracking.
/// Favor accumulation is handled by the Arda L4 <c>NpcStateComposer</c>.
/// </summary>
public sealed class FavorIngestionService : BackgroundService
{
    private readonly CalibrationService _calibration;
    private readonly IDiagnosticsSink? _diag;
    private readonly IDisposable? _giftSub;
    private Dispatcher? _dispatcher;

    public FavorIngestionService(
        IDomainEventSubscriber bus,
        CalibrationService calibration,
        IActiveCharacterService activeChar,
        SettingsAutoSaver<ArwenSettings> autoSaver,
        IDiagnosticsSink? diag = null)
    {
        _calibration = calibration;
        _diag = diag;
        _ = activeChar;
        _ = autoSaver;

        _giftSub = bus.Subscribe<ArdaGiftAccepted>(OnArdaGiftAccepted);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _diag?.Info("Arwen.Ingestion",
            "Subscribing to Arda domain bus (GiftAccepted) for calibration");

        _dispatcher = Application.Current?.Dispatcher;

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on host stop */ }
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
        _giftSub?.Dispose();
        base.Dispose();
    }
}
