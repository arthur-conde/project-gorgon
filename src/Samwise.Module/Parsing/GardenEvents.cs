using Gorgon.Shared.Logging;

namespace Samwise.Parsing;

public abstract record GardenEvent(DateTime Timestamp) : LogEvent(Timestamp);

public sealed record PlayerLogin(DateTime Timestamp, string CharName) : GardenEvent(Timestamp);
public sealed record SetPetOwner(DateTime Timestamp, string EntityId) : GardenEvent(Timestamp);
public sealed record AppearanceLoop(DateTime Timestamp, string ModelName) : GardenEvent(Timestamp);
public sealed record UpdateDescription(DateTime Timestamp, string PlotId, string Title, string Description, string Action, double Scale) : GardenEvent(Timestamp);
public sealed record StartInteraction(DateTime Timestamp, string PlotId, string Target) : GardenEvent(Timestamp);
public sealed record AddItem(DateTime Timestamp, string ItemId, string ItemName) : GardenEvent(Timestamp);
public sealed record UpdateItemCode(DateTime Timestamp, string ItemId) : GardenEvent(Timestamp);
public sealed record GardeningXp(DateTime Timestamp) : GardenEvent(Timestamp);
public sealed record ScreenTextError(DateTime Timestamp) : GardenEvent(Timestamp);
