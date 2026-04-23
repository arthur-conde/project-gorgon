using Gorgon.Shared.Logging;

namespace Samwise.Parsing;

public abstract record GardenEvent(DateTime Timestamp) : LogEvent(Timestamp);

public sealed record SetPetOwner(DateTime Timestamp, string EntityId) : GardenEvent(Timestamp);
public sealed record AppearanceLoop(DateTime Timestamp, string ModelName, double Scale = 0.1) : GardenEvent(Timestamp);
public sealed record UpdateDescription(DateTime Timestamp, string PlotId, string Title, string Description, string Action, double Scale) : GardenEvent(Timestamp);
public sealed record StartInteraction(DateTime Timestamp, string PlotId, string Target) : GardenEvent(Timestamp);
public sealed record AddItem(DateTime Timestamp, string ItemId, string ItemName) : GardenEvent(Timestamp);
public sealed record UpdateItemCode(DateTime Timestamp, string ItemId) : GardenEvent(Timestamp);
public sealed record DeleteItem(DateTime Timestamp, string ItemId) : GardenEvent(Timestamp);
public sealed record GardeningXp(DateTime Timestamp) : GardenEvent(Timestamp);
public sealed record ScreenTextError(DateTime Timestamp) : GardenEvent(Timestamp);

/// <summary>
/// Fired when the player tries to plant a seed but has already hit the slot
/// cap for that crop's family. The seed's display name (e.g. "Barley Seeds")
/// drives the lookup to resolve which family was capped.
/// </summary>
public sealed record PlantingCapReached(DateTime Timestamp, string SeedDisplayName) : GardenEvent(Timestamp);
