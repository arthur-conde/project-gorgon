using System.Collections.Generic;

namespace Mithril.Reference.Models.Misc;

/// <summary>
/// One entry from <c>storagevaults.json</c>; keyed by NPC internal name
/// (sometimes prefixed with <c>"*"</c> for account-wide vaults).
/// </summary>
public sealed class StorageVault
{
    public string? Area { get; set; }
    public int ID { get; set; }
    public string? NpcFriendlyName { get; set; }
    public string? Grouping { get; set; }

    /// <summary>Map from favor-level name (e.g. <c>"BestFriends"</c>) to slot count at that favor.</summary>
    public IReadOnlyDictionary<string, int>? Levels { get; set; }

    public int? NumSlots { get; set; }
    public IReadOnlyList<string>? RequiredItemKeywords { get; set; }
    public string? RequirementDescription { get; set; }
    public bool? HasAssociatedNpc { get; set; }

    /// <summary>Dict-or-array; coerced to a list by SingleOrArrayConverter.</summary>
    public IReadOnlyList<StorageRequirement>? Requirements { get; set; }

    public string? SlotAttribute { get; set; }
    public string? NumSlotsScriptAtomic { get; set; }
    public int? NumSlotsScriptAtomicMaxValue { get; set; }
    public int? NumSlotsScriptAtomicMinValue { get; set; }

    /// <summary>Event-gated overrides on <see cref="Levels"/>; rare (1 entry).</summary>
    public IReadOnlyDictionary<string, int>? EventLevels { get; set; }
}

/// <summary>
/// Polymorphic prerequisite row on a <see cref="StorageVault.Requirements"/>.
/// Separate from <c>QuestRequirement</c> by the same field-set-divergence rule
/// that separated <c>RecipeRequirement</c> — <c>ServerRulesFlagSet</c> is
/// storage-specific, and overlapping types may add fields in future patches.
/// </summary>
public abstract class StorageRequirement
{
    public string T { get; set; } = "";
}

public sealed class UnknownStorageRequirement : StorageRequirement, IUnknownDiscriminator
{
    public string DiscriminatorValue { get; set; } = "";
}

public sealed class StorageInteractionFlagSetRequirement : StorageRequirement
{
    public string? InteractionFlag { get; set; }
}

public sealed class StorageIsLongtimeAnimalRequirement : StorageRequirement { }

public sealed class StorageIsWardenRequirement : StorageRequirement { }

public sealed class StorageQuestCompletedRequirement : StorageRequirement
{
    public string? Quest { get; set; }
}

public sealed class StorageServerRulesFlagSetRequirement : StorageRequirement
{
    public string? Flag { get; set; }
}
