using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using Microsoft.Extensions.Hosting;
using Saruman.Domain;
using Saruman.Parsing;

namespace Saruman.Services;

public sealed class SarumanChatIngestionService : BackgroundService
{
    private readonly IChatLogStream _stream;
    private readonly WordOfPowerChatParser _parser;
    private readonly SarumanCodebookService _codebook;
    private readonly ModuleGate _gate;

    public SarumanChatIngestionService(
        IChatLogStream stream,
        WordOfPowerChatParser parser,
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
            if (_parser.TryParse(raw.Line, raw.Timestamp) is WordOfPowerSpoken s)
                _codebook.MarkSpent(s.Code, s.Timestamp);
        }
    }
}
