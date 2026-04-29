using System.Collections.Generic;

namespace Mithril.Reference.Models.Quests;

/// <summary>
/// One step a quest requires the player to complete. Modelled as a single flat
/// class — although the JSON has 38 distinct <see cref="Type"/> values
/// ("Kill", "Collect", "Scripted", "UseRecipe", …), the field set per type is
/// largely overlapping and the discriminator is more of a category tag than a
/// shape selector. Keep <see cref="Type"/> as a string and switch on it at
/// consumption time.
/// </summary>
public sealed class QuestObjective
{
    public string? Type { get; set; }
    public string? Description { get; set; }
    public int Number { get; set; }

    /// <summary>String-or-array in JSON; coerced to a list by SingleOrArrayConverter.</summary>
    public IReadOnlyList<string>? Target { get; set; }

    public string? ItemName { get; set; }
    public int? GroupId { get; set; }

    /// <summary>Dict-or-array in JSON; coerced to a list by SingleOrArrayConverter.</summary>
    public IReadOnlyList<QuestRequirement>? Requirements { get; set; }

    public string? InternalName { get; set; }
    public string? AbilityKeyword { get; set; }
    public bool? IsHiddenUntilEarlierObjectivesComplete { get; set; }
    public string? ItemKeyword { get; set; }
    public string? AllowedFishingZone { get; set; }
    public IReadOnlyList<string>? InteractionFlags { get; set; }
    public string? ResultItemKeyword { get; set; }
    public string? Item { get; set; }
    public string? NumToDeliver { get; set; }
    public string? StringParam { get; set; }
    public string? AnatomyType { get; set; }
    public string? FishConfig { get; set; }
    public string? MinAmount { get; set; }
    public string? NumTargets { get; set; }
    public string? Skill { get; set; }
    public string? DamageType { get; set; }
    public string? MinFavorReceived { get; set; }
    public string? MaxFavorReceived { get; set; }
    public string? MaxAmount { get; set; }
    public string? BehaviorId { get; set; }
    public IReadOnlyList<string>? CausesOfDeath { get; set; }
    public string? MonsterTypeTag { get; set; }
}
