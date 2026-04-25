using Mithril.Shared.Logging;

namespace Saruman.Domain;

public sealed record WordOfPowerDiscovered(
    DateTime Timestamp,
    string Code,
    string EffectName,
    string Description) : LogEvent(Timestamp);

public sealed record WordOfPowerSpoken(
    DateTime Timestamp,
    string Speaker,
    string Code) : LogEvent(Timestamp);
