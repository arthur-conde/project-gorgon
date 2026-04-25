namespace Mithril.Shared.Logging;

public interface IPlayerLogStream
{
    IAsyncEnumerable<RawLogLine> SubscribeAsync(CancellationToken ct);
}
