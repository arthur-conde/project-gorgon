using Arda.Abstractions.Logs;

namespace Arda.Dispatch;

/// <summary>
/// The L2 dispatch loop. Pulls lines from an <see cref="ILogLineSource"/>,
/// extracts verbs via <see cref="VerbExtractor"/>, and routes to the
/// <see cref="DispatchTable"/>.
/// </summary>
internal sealed class WorldDriver : IWorldDriver
{
    private readonly ILogLineSource _source;
    private readonly DispatchTable _dispatch;

    public WorldDriver(ILogLineSource source, DispatchTable dispatch)
    {
        _source = source;
        _dispatch = dispatch;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await foreach (var line in _source.Lines(ct))
        {
            var logSpan = line.Log.AsSpan();
            var verbSpan = VerbExtractor.Extract(logSpan);
            _dispatch.Dispatch(verbSpan, logSpan, line.Log, line.Metadata);
        }
    }
}
