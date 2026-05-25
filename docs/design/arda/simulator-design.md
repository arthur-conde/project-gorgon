World frame
* Applies to the system (System frame), the local player (Player frame), or a combat actor (frame)

System frame
* Facts about the game world at large
   * Area/Map
   * Someone logging in (needs verification)

Player frame
* Facts about the current player

Combat actor frame
* Dont really care about this guy rn

World Input Pipe
* Abbreviated as world.in
* Describes the path from Player Log Source to the world

World Filter
* Filters envelopes
* Dumps log entries that lack the timestamp expected of a world frame

World
* The Project Gorgon world simulation
* Must be deterministic

World Internal Pipe
* abbreviated as world.px
* a chain of state machines that interpret changes

```csharp
struct FrameMetadata
{
   string? Actor;
   string
}

interface ISimulationFrame
{
   LogLineMetadata Metadata;
}

class LevelFrame : ISimulationFrame
{
   string AreaKey;
   LogLineMetadata Metadata;
}

interface ISimulationActorFrame : ISimulationFrame
{
   string Actor; // `entity_N` or LocalPlayer
}

// [04:02:19] LocalPlayer: ProcessCombatModeStatus(EnemiesHateYou, [28203619,28074242,])
// [04:02:35] LocalPlayer: ProcessCombatModeStatus(UsedAbilityRecently, [])
// [04:03:32] LocalPlayer: ProcessCombatModeStatus(NotInCombat, [])
class CombatStatusFrame : ISimulationActorFrame
{
   string Status; // enum, "NotInCombat", "EnemiesHateYou","UsedAbilityRecently"
   long[] InstanceIds; // array, never null, usually empty
}

interface IWorld
{
   IStateMachine[] StateMachines;
}

interface IStateMachine
{
   Handle(ISimulationFrame frame);
}
```

World sim flow
1. An envelope arrives from the [log source](log-source.md) from the log source
2. World filters are collected and applied against the envelope
    * Reject any envelopes without a timestamp
      * Assumed to be noise
    * Rejected logged to a sink, or diagnostic view, if desired (Rx.net?)
3. An envelope is read from the pipe
    * Preserves the envelope metadata
4. An envelope is deserialized into a frame
5. A frame is pushed into world.in
 - world boundary -
7. A frame is read from world.in to the world's state machine pipe, world.px
8. For every state machine in the world, the frame is passed
   1. The state machine processes the frame, or does nothing
   2. The state machine emits a domain event through the world's output pipe, or world.out
 - world boundary -
9. Domain events exit on world out
 - composition boundary - 
10. Composers consume domain events from world, chat, access reports, or read reference data and format it for the user

Suggested state machines:
* World
   * NPC
      * Inputs
         * ProcessStartInteraction
         * ProcessFavorDelta
   * Outputs
         * Gifts
   * Map
      * Inputs
         * System: LOADING LEVEL AreaEltibule
         * ProcessMapPinAdd
         * ProcessAddPlayer
         * System: !!! Initializing area! (502934): AreaEltibule
         * System: SPAWNING LOCAL PLAYER AT (1165.34, 128.22, 2912.12)
         * ProcessMapFx
      * Outputs
         * Player Position Set
         * Map changed
         * Pin CRUD
      * Owns
         * Weather
         * Pins
      * References
         * areas.json
   * Inventory
   * Inputs
         * ProcessAddItem
         * ProcessDeleteItem
         * ProcessUpdateItemCode
      * Outputs
         * Many
      * Owns
         * Inventory
         * Storage
   * Player
      * Inputs
         * Many
      * Outputs
         * Many
      * Owns
         * effects
            * reference: effects.json
         * attributes
            * reference: attributes.json
         * recipes
         * skills
   * Words of Power
   * Environment
      * Inputs
         * Any
      * Outputs
         * Time change
         * Time of Day change
         * Game day change
            * Contrast to day change in the world - world clock ticks faster than wall clock
            * Represents a change in day (every wall clock hours) that lines up with the server's daily reset
      * Owns
         * Moon
