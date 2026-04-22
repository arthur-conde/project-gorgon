using Gorgon.Shared.Logging;
using Gorgon.Shared.Modules;
using Microsoft.Extensions.Hosting;
using Saruman.Domain;
using Saruman.Parsing;

namespace Saruman.Services;

public sealed class SarumanDiscoveryIngestionService : BackgroundService
{
    private readonly IPlayerLogStream _stream;
    private readonly WordOfPowerDiscoveredParser _parser;
    private readonly SarumanCodebookService _codebook;
    private readonly ModuleGate _gate;

    public SarumanDiscoveryIngestionService(
        IPlayerLogStream stream,
        WordOfPowerDiscoveredParser parser,
        SarumanCodebookService codebook,
        ModuleGates gates)
    {
        _stream = stream;
        _parser = parser;
        _codebook = codebook;
        _gate = gates.For("saruman");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _gate.WaitAsync(stoppingToken).ConfigureAwait(false);

        await foreach (var raw in _stream.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            if (_parser.TryParse(raw.Line, raw.Timestamp) is WordOfPowerDiscovered d)
                _codebook.RecordDiscovery(d);
        }
    }
}
