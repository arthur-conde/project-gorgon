namespace Gorgon.Shared.Logging;

public interface IPlayerLogStream
{
    IAsyncEnumerable<RawLogLine> SubscribeAsync(CancellationToken ct);
}
