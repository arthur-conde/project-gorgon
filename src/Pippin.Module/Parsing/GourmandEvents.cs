using Mithril.Shared.Logging;

namespace Pippin.Parsing;

public abstract record GourmandEvent(DateTime Timestamp) : LogEvent(Timestamp);

public sealed record FoodsConsumedReport(
    DateTime Timestamp,
    IReadOnlyList<FoodConsumedEntry> Foods) : GourmandEvent(Timestamp);

public sealed record FoodConsumedEntry(
    string Name,
    int Count,
    IReadOnlyList<string> Tags);
