using System.Collections.Generic;

namespace Mithril.Reference.Models.Npcs;

/// <summary>
/// Polymorphic entry in an NPC's <c>Services</c> array. Discriminated by the
/// JSON <c>Type</c> field (note: not <c>T</c> like quest/recipe requirements
/// — NPC services use a different discriminator name).
/// </summary>
public abstract class NpcService
{
    public string Type { get; set; } = "";

    /// <summary>Minimum favor level required to access this service. Always present.</summary>
    public string? Favor { get; set; }
}

/// <summary>Sentinel for any <c>Type</c> value not covered by a concrete subclass.</summary>
public sealed class UnknownNpcService : NpcService, IUnknownDiscriminator
{
    public string DiscriminatorValue { get; set; } = "";
}

public sealed class AnimalHusbandryService : NpcService { }

public sealed class BarterService : NpcService
{
    public IReadOnlyList<string>? AdditionalUnlocks { get; set; }
}

public sealed class ConsignmentService : NpcService
{
    public IReadOnlyList<string>? ItemTypes { get; set; }
    public IReadOnlyList<string>? Unlocks { get; set; }
}

public sealed class GuildQuestsService : NpcService { }

public sealed class InstallAugmentsService : NpcService
{
    /// <summary>Dash-separated range strings (e.g. <c>"0-60"</c>, <c>"21-40"</c>); parse at consumption time.</summary>
    public IReadOnlyList<string>? LevelRange { get; set; }
}

public sealed class StablesService : NpcService { }

public sealed class StorageService : NpcService
{
    public IReadOnlyList<string>? ItemDescs { get; set; }
    public IReadOnlyList<string>? SpaceIncreases { get; set; }
}

public sealed class StoreService : NpcService
{
    public IReadOnlyList<string>? CapIncreases { get; set; }
}

public sealed class TrainingService : NpcService
{
    public IReadOnlyList<string>? Skills { get; set; }
    public IReadOnlyList<string>? Unlocks { get; set; }
}
