using Mithril.Shared.Diagnostics;
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
    private readonly ThrottledWarn _warn;

    public SarumanChatIngestionService(
        IChatLogStream stream,
        WordOfPowerChatParser parser,
        SarumanCodebookService codebook,
        ModuleGates gates,
        IDiagnosticsSink? diag = null)
    {
        _stream = stream;
        _parser = parser;
        _codebook = codebook;
        _gate = gates.For("saruman");
        _warn = new ThrottledWarn(diag, "Saruman.Ingestion");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _gate.WaitAsync(stoppingToken).ConfigureAwait(false);

        await foreach (var raw in _stream.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                if (_parser.TryParse(raw.Line, raw.Timestamp.UtcDateTime) is WordOfPowerSpoken s)
                    _codebook.MarkSpent(s.Code, s.Timestamp);
            }
            catch (Exception ex) { _warn.Warn($"Ingestion error: {ex.Message}"); }
        }
    }
}
