using Arda.Abstractions.Logs;

namespace Arda.Dispatch;

/// <summary>
/// The L2 dispatch loop. Pulls lines from an <see cref="ILogLineSource"/>,
/// extracts verbs via <see cref="VerbExtractor"/>, and routes to the
/// <see cref="DispatchTable"/>. Optionally signals when the stream transitions
/// from replay to live (first line with <c>IsReplay = false</c>).
/// </summary>
internal sealed class WorldDriver : IWorldDriver
{
    private readonly ILogLineSource _source;
    private readonly DispatchTable _dispatch;
    private readonly Action? _onLiveTransition;

    public WorldDriver(ILogLineSource source, DispatchTable dispatch, Action? onLiveTransition = null)
    {
        _source = source;
        _dispatch = dispatch;
        _onLiveTransition = onLiveTransition;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var liveSignalled = _onLiveTransition is null;

        await foreach (var line in _source.Lines(ct))
        {
            if (!liveSignalled && !line.Metadata.IsReplay)
            {
                liveSignalled = true;
                _onLiveTransition!();
            }

            var parsed = VerbExtractor.Parse(line.Log.AsSpan());
            _dispatch.Dispatch(parsed, line.Log, line.Metadata);
        }

        if (!liveSignalled)
            _onLiveTransition!();
    }
}
