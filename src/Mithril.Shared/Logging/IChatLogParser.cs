namespace Mithril.Shared.Logging;

public interface IChatLogParser
{
    LogEvent? TryParse(string line, DateTime timestamp);
}
