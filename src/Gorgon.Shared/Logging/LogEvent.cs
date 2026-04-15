namespace Gorgon.Shared.Logging;

public abstract record LogEvent(DateTime Timestamp);

public sealed record RawLogLine(DateTime Timestamp, string Line) : LogEvent(Timestamp);
