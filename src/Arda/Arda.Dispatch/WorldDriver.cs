using Arda.Abstractions.Logs;
using Microsoft.Extensions.Logging;

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
    private readonly IReadOnlyList<ILineObserver> _observers;
    private readonly ILogger? _logger;
    private readonly string? _sourceFamily;
    private long _lineCount;

    public WorldDriver(
        ILogLineSource source,
        DispatchTable dispatch,
        Action? onLiveTransition = null,
        IReadOnlyList<ILineObserver>? observers = null,
        ILogger? logger = null,
        string? sourceFamily = null)
    {
        _source = source;
        _dispatch = dispatch;
        _onLiveTransition = onLiveTransition;
        _observers = observers ?? [];
        _logger = logger;
        _sourceFamily = sourceFamily;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var liveSignalled = _onLiveTransition is null;

        await foreach (var line in _source.Lines(ct))
        {
            _lineCount++;

            if (!liveSignalled && !line.Metadata.IsReplay)
            {
                liveSignalled = true;
                _logger?.LogInformation(
                    "Replay to live transition for {SourceFamily}",
                    _sourceFamily ?? "unknown");
                _onLiveTransition!();
            }

            foreach (var observer in _observers)
                observer.Observe(line.Log, line.Metadata);

            var parsed = VerbExtractor.Parse(line.Log.AsSpan());
            _dispatch.Dispatch(parsed, line.Log, line.Metadata);
        }

        if (!liveSignalled)
        {
            _logger?.LogWarning(
                "Live transition forced at end of stream for {SourceFamily} ({LineCount} lines processed)",
                _sourceFamily ?? "unknown",
                _lineCount);
            _onLiveTransition!();
        }

        _logger?.LogInformation(
            "World driver completed for {SourceFamily} ({LineCount} lines processed)",
            _sourceFamily ?? "unknown",
            _lineCount);
    }
}
