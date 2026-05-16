using System;
using FluentAssertions;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Serialization.Discriminators;
using Mithril.Shared.Wpf.Query;
using Xunit;

namespace Mithril.Shared.Tests.Wpf.Query;

/// <summary>
/// <see cref="PolymorphicSchemaClassifier"/> over the real reference hierarchies.
/// The load-bearing facts: <c>QuestRequirement</c> is <em>Mandatory</em> because
/// <c>Level</c> is <c>string?</c> on some subtypes and <c>int?</c> on another;
/// <c>NpcService</c> is <em>Optional</em> (no type collisions); every registered
/// family classifies; results are cached.
/// </summary>
public class PolymorphicSchemaClassifierTests
{
    [Fact]
    public void QuestRequirement_is_Mandatory_with_Level_colliding()
    {
        var c = PolymorphicSchemaClassifier.Classify(typeof(QuestRequirement));

        c.Should().NotBeNull();
        c!.Mode.Should().Be(NarrowingMode.Mandatory);
        c.DiscriminatorField.Should().Be("T");
        c.CollidingProps.Should().Contain("Level");
        // The split that forces Mandatory: same name, different concrete type.
        c.ResolvePropType("MinSkillLevel", "Level").Should().Be(typeof(string));
        c.ResolvePropType("MinCombatSkillLevel", "Level").Should().Be(typeof(int?));
        // Subtype-specific scalar declared on exactly one subtype
        // (AllowSkill is only on ActiveCombatSkillRequirement; Skill is on two
        // subtypes with the same type, so it is NOT single-subtype).
        c.SingleSubtypeProps.Should().Contain("AllowSkill");
        c.SingleSubtypeProps.Should().NotContain("Skill");
    }

    [Fact]
    public void NpcService_is_Optional_no_collisions()
    {
        var c = PolymorphicSchemaClassifier.Classify(typeof(NpcService));

        c.Should().NotBeNull();
        c!.Mode.Should().Be(NarrowingMode.Optional);
        c.DiscriminatorField.Should().Be("Type");
        c.CollidingProps.Should().BeEmpty();
        // CapIncreases lives only on StoreService → single-subtype (drives the
        // optional soft warning).
        c.SingleSubtypeProps.Should().Contain("CapIncreases");
        // Unlocks is on Consignment + Training with the SAME type → not single.
        c.SingleSubtypeProps.Should().NotContain("Unlocks");
    }

    [Fact]
    public void Every_registered_hierarchy_classifies()
    {
        DiscriminatorRegistry.All.Should().HaveCount(7);
        foreach (var h in DiscriminatorRegistry.All)
        {
            PolymorphicSchemaClassifier.Classify(h.BaseType)
                .Should().NotBeNull($"{h.BaseType.Name} is a registered polymorphic base");
        }
    }

    [Fact]
    public void Unregistered_type_classifies_to_null()
    {
        PolymorphicSchemaClassifier.Classify(typeof(string)).Should().BeNull();
        PolymorphicSchemaClassifier.Classify(typeof(PolymorphicSchemaClassifierTests))
            .Should().BeNull();
    }

    [Fact]
    public void Classification_is_cached()
    {
        var a = PolymorphicSchemaClassifier.Classify(typeof(QuestRequirement));
        var b = PolymorphicSchemaClassifier.Classify(typeof(QuestRequirement));
        ReferenceEquals(a, b).Should().BeTrue();
    }
}
