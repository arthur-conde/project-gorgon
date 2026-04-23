using Gorgon.Shared.Diagnostics;
using Gorgon.Shared.Logging;
using Gorgon.Shared.Modules;
using Microsoft.Extensions.Hosting;
using Smaug.Domain;
using Smaug.Parsing;

namespace Smaug.State;

/// <summary>
/// Subscribes to Player.log once the Smaug module gate opens, parses vendor-related
/// lines, and feeds recorded sells into <see cref="PriceCalibrationService"/>.
/// </summary>
public sealed class VendorIngestionService : BackgroundService
{
    private readonly IPlayerLogStream _stream;
    private readonly VendorLogParser _parser;
    private readonly PriceCalibrationService _calibration;
    private readonly VendorSellContext _context;
    private readonly IDiagnosticsSink? _diag;
    private readonly ModuleGate _gate;

    public VendorIngestionService(
        IPlayerLogStream stream,
        VendorLogParser parser,
        PriceCalibrationService calibration,
        VendorSellContext context,
        ModuleGates gates,
        IDiagnosticsSink? diag = null)
    {
        _stream = stream;
        _parser = parser;
        _calibration = calibration;
        _context = context;
        _diag = diag;
        _gate = gates.For("smaug");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _diag?.Info("Smaug", "Waiting for module gate…");
        await _gate.WaitAsync(stoppingToken).ConfigureAwait(false);
        _diag?.Info("Smaug", "Gate opened — subscribing to Player.log for vendor events");

        await foreach (var raw in _stream.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            var evt = _parser.TryParse(raw.Line, raw.Timestamp);
            if (evt is null) continue;

            switch (evt)
            {
                case CivicPrideUpdated cp:
                    _context.CivicPrideLevel = cp.EffectiveLevel;
                    _diag?.Trace("Smaug.Parse", $"CivicPride level={cp.EffectiveLevel} (raw={cp.Raw}+bonus={cp.Bonus})");
                    break;

                case NpcInteractionStarted started:
                    _context.RememberEntity(started.EntityId, started.NpcKey);
                    break;

                case VendorScreenOpened screen:
                    _context.OnVendorScreenOpened(screen.EntityId, screen.FavorTier);
                    _diag?.Trace("Smaug.Parse",
                        $"VendorScreen entity={screen.EntityId} npc={_context.ActiveNpcKey ?? "?"} tier={screen.FavorTier}");
                    break;

                case VendorItemSold sold:
                    if (!_context.IsReadyToRecord)
                    {
                        _diag?.Trace("Smaug.Parse",
                            $"Sell of {sold.InternalName} for {sold.Price} skipped — no active vendor context");
                        break;
                    }
                    _calibration.RecordObservation(
                        _context.ActiveNpcKey!,
                        sold.InternalName,
                        sold.Price,
                        _context.ActiveFavorTier!,
                        _context.CivicPrideLevel,
                        DateTimeOffset.UtcNow);
                    break;
            }
        }
    }
}
