using System.Collections.Generic;

namespace Mithril.Reference.Serialization.Discriminators;

/// <summary>
/// Public catalogue of every discriminated-union hierarchy in the reference model.
/// Aggregates the per-family <c>*Discriminators</c> projections so cross-assembly
/// consumers (the query engine's polymorphic-schema classifier) can enumerate
/// subtypes without reaching into serializer internals — the maps themselves stay
/// private to their family classes.
/// </summary>
/// <remarks>
/// <see cref="Models.Recipes.RecipeIngredient"/> is intentionally absent: it is a
/// field-presence union with no JSON discriminator string, so it has no
/// discriminator map to project. The seven entries here are the discriminator-keyed
/// families.
/// </remarks>
public static class DiscriminatorRegistry
{
    public static IReadOnlyList<PolymorphicHierarchy> All { get; } = new[]
    {
        NpcDiscriminators.Hierarchy,
        AbilityDiscriminators.Hierarchy,
        QuestDiscriminators.RequirementHierarchy,
        QuestDiscriminators.RewardHierarchy,
        RecipeDiscriminators.Hierarchy,
        SourceDiscriminators.Hierarchy,
        StorageDiscriminators.Hierarchy,
    };
}
