using Arda.Abstractions.Logs;

namespace Arda.Dispatch;

/// <summary>
/// Receives every line before verb dispatch. Used by the Calendar handler
/// to track timestamp advancement and by the AppearanceObserver to match
/// non-verb patterns that can't be verb-dispatched.
/// </summary>
public interface ILineObserver
{
    void Observe(string log, LogLineMetadata metadata);
}
