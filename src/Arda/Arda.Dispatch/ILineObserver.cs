using Arda.Abstractions.Logs;

namespace Arda.Dispatch;

/// <summary>
/// Receives every line before verb dispatch. Used by the Calendar handler
/// to track timestamp advancement without verb registration.
/// </summary>
public interface ILineObserver
{
    void Observe(LogLineMetadata metadata);
}
