using System.Collections.Generic;

namespace Mithril.Reference.Models.Misc;

/// <summary>
/// One entry from <c>abilitykeywords.json</c>. The file's top-level shape is
/// a JSON array of these records (not the dictionary envelope used elsewhere).
/// Each entry binds attribute lists (crit chance/damage modifiers, ability
/// keywords) under a precondition described by <see cref="MustHaveAbilityKeywords"/>.
/// </summary>
public sealed class AbilityKeyword
{
    public IReadOnlyList<string>? AttributesThatDeltaCritChance { get; set; }
    public IReadOnlyList<string>? AttributesThatModCritDamage { get; set; }
    public IReadOnlyList<string>? MustHaveAbilityKeywords { get; set; }
}
