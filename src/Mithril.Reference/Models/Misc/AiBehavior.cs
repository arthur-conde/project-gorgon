using System.Collections.Generic;

namespace Mithril.Reference.Models.Misc;

/// <summary>
/// One AI behaviour entry from <c>ai.json</c>; keyed by behaviour name
/// (e.g. <c>"AcidAura"</c>).
/// </summary>
public sealed class AiBehavior
{
    /// <summary>Map from ability internal name to its <see cref="AiAbilityRange"/> level gate.</summary>
    public IReadOnlyDictionary<string, AiAbilityRange>? Abilities { get; set; }

    public string? Strategy { get; set; }
    public bool? Swimming { get; set; }
    public string? MobilityType { get; set; }
    public bool? Flying { get; set; }
    public string? Comment { get; set; }

    /// <summary>Int in 16 entries, float in 10; modelled as double for tolerance.</summary>
    public double? MinDelayBetweenAbilities { get; set; }

    public bool? UncontrolledPet { get; set; }
    public string? Description { get; set; }
    public bool? UseAbilitiesWithoutEnemyTarget { get; set; }
    public int? FlyOffset { get; set; }
    public bool? ServerDriven { get; set; }
}

/// <summary>Level-gate range for one ability inside <see cref="AiBehavior.Abilities"/>.</summary>
public sealed class AiAbilityRange
{
    public int? minLevel { get; set; }
    public int? maxLevel { get; set; }
}
