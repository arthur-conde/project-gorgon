namespace Mithril.Shared.Logging;

/// <summary>
/// Centralized tail of the game's ChatLogs directory. Emits every new line
/// written to any file in the directory, including files that are created
/// after subscription (so daily-log rollovers are transparent).
/// </summary>
public interface IChatLogStream
{
    IAsyncEnumerable<RawLogLine> SubscribeAsync(CancellationToken ct);
}
