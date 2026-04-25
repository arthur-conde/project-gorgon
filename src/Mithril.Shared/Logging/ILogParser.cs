namespace Mithril.Shared.Logging;

public interface ILogParser
{
    LogEvent? TryParse(string line, DateTime timestamp);
}
