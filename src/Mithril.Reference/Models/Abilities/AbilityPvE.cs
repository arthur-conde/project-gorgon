using System.Collections.Generic;

namespace Mithril.Reference.Models.Abilities;

/// <summary>
/// Per-version stat block for an ability — the PvE flavor (PvP would be a
/// parallel block but is not present in the bundled data).
/// </summary>
public sealed class AbilityPvE
{
    public int PowerCost { get; set; }
    public int Range { get; set; }
    public IReadOnlyList<string>? AttributesThatDeltaDamage { get; set; }
    public IReadOnlyList<string>? AttributesThatModDamage { get; set; }
    public int? Damage { get; set; }
    public IReadOnlyList<string>? AttributesThatModBaseDamage { get; set; }
    public IReadOnlyList<string>? AttributesThatDeltaTaunt { get; set; }
    public IReadOnlyList<AbilityDoT>? DoTs { get; set; }
    public IReadOnlyList<string>? AttributesThatModCritDamage { get; set; }
    public IReadOnlyList<AbilitySpecialValue>? SpecialValues { get; set; }

    /// <summary>Int in 1077 entries, float in 2; modelled as double for tolerance.</summary>
    public double? AoE { get; set; }

    public IReadOnlyList<string>? AttributesThatDeltaAccuracy { get; set; }
    public int? RageCost { get; set; }
    public int? RageMultiplier { get; set; }
    public double? CritDamageMod { get; set; }

    /// <summary>Int in 414 entries, float in 2; modelled as double.</summary>
    public double? RageCostMod { get; set; }

    public IReadOnlyList<string>? AttributesThatModRage { get; set; }
    public IReadOnlyList<string>? AttributesThatDeltaRage { get; set; }
    public int? ExtraDamageIfTargetVulnerable { get; set; }
    public IReadOnlyList<string>? AttributesThatModTaunt { get; set; }
    public int? HealthSpecificDamage { get; set; }
    public IReadOnlyList<string>? AttributesThatDeltaRange { get; set; }
    public int? TauntDelta { get; set; }

    /// <summary>Float in 195 entries, int in 1; modelled as double.</summary>
    public double? Accuracy { get; set; }

    public int? ArmorSpecificDamage { get; set; }
    public IReadOnlyList<string>? AttributesThatDeltaDamageIfTargetIsVulnerable { get; set; }
    public int? RageBoost { get; set; }
    public IReadOnlyList<string>? AttributesThatDeltaAoE { get; set; }
    public int? TempTauntDelta { get; set; }
    public int? ArmorMitigationRatio { get; set; }

    /// <summary>Int in 2 entries, float in 5; modelled as double.</summary>
    public double? TauntMod { get; set; }

    public IReadOnlyList<string>? AttributesThatDeltaTempTaunt { get; set; }
}
