using System.Windows;
using Gorgon.Shared.Logging;
using Microsoft.Extensions.Hosting;
using Samwise.Parsing;

namespace Samwise.State;

public sealed class GardenIngestionService : BackgroundService
{
    private readonly IPlayerLogStream _stream;
    private readonly GardenLogParser _parser;
    private readonly GardenStateMachine _state;

    public GardenIngestionService(IPlayerLogStream stream, GardenLogParser parser, GardenStateMachine state)
    {
        _stream = stream;
        _parser = parser;
        _state = state;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var raw in _stream.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            var evt = _parser.TryParse(raw.Line, raw.Timestamp);
            if (evt is GardenEvent ge)
            {
                Dispatch(() => _state.Apply(ge));
            }
        }
    }

    private static void Dispatch(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.InvokeAsync(a);
    }
}
