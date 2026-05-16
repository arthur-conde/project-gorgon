using FluentAssertions;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.ViewModels;

/// <summary>
/// Covers the requirement projection that closes the planner-punt ↔ Silmarillion-display
/// blind spot: the gates <c>CrossSkillPlanner</c> deliberately does not pursue
/// (<c>docs/planner-recipe-field-consumption.md</c>) must be visible here, or the
/// user-asserted contract is unverifiable. Synthetic fixtures verify projection shape.
/// </summary>
public sealed class RecipeRequirementProjectorTests
{
    private static (IReferenceNavigator Nav, IEntityNameResolver Resolver) Deps(bool recipesTabbed = true)
    {
        var targets = recipesTabbed
            ? new[] { (IReferenceKindTarget)new RecipeTarget() }
            : Array.Empty<IReferenceKindTarget>();
        return (new SilmarillionReferenceNavigator(targets), new InternalNameResolver());
    }

    /// <summary>Minimal Recipe-kind target so <c>navigator.CanOpen(Recipe)</c> is true.</summary>
    private sealed class RecipeTarget : IReferenceKindTarget
    {
        public EntityKind Kind => EntityKind.Recipe;
        public int TabIndex => 0;
        public bool TrySelectByInternalName(string internalName) => true;
        public bool TryOpenInWindow() => true;
    }

    /// <summary>Resolver that echoes the internal name — chip display assertions only
    /// care that the reference/navigability is wired, not the friendly string.</summary>
    private sealed class InternalNameResolver : IEntityNameResolver
    {
        public string Resolve(EntityRef reference) => reference.InternalName;
    }

    /// <summary>No strings_all entries ⇒ tag projection falls back to Humanise.</summary>
    private static readonly IReadOnlyDictionary<string, string> NoStrings =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [Fact]
    public void EmptyOrNull_YieldsNoLinesOrChips()
    {
        var (nav, resolver) = Deps();
        var (lines, chips) = RecipeRequirementProjector.Build(null, "Self", nav, resolver, NoStrings);
        lines.Should().BeEmpty();
        chips.Should().BeEmpty();
    }

    [Fact]
    public void CyclicalAndUserAssertedGates_RenderAsReadableLines()
    {
        var (nav, resolver) = Deps();
        var reqs = new RecipeRequirement[]
        {
            new FullMoonRecipeRequirement { T = "FullMoon" },
            new MoonPhaseRecipeRequirement { T = "MoonPhase", MoonPhase = "WaningCrescent" },
            new WeatherRequirement { T = "Weather", ClearSky = true },
            new TimeOfDayRecipeRequirement { T = "TimeOfDay", MinHour = 20, MaxHour = 4 },
            new PetCountRecipeRequirement { T = "PetCount", PetTypeTag = "CowPet", MinCount = 2, MaxCount = 2 },
            new HasEffectKeywordRecipeRequirement { T = "HasEffectKeyword", Keyword = "Sandstorm" },
            new HasHandsRequirement { T = "HasHands" },
            new IsLycanthropeRequirement { T = "IsLycanthrope" },
            new HasGuildHallRequirement { T = "HasGuildHall" },
        };

        var (lines, chips) = RecipeRequirementProjector.Build(reqs, "Self", nav, resolver, NoStrings);

        chips.Should().BeEmpty();
        lines.Should().Equal(
            "Only during the full moon.",
            "Only during the waning crescent moon.",
            "Only when the sky is clear.",
            "Only between 20:00 and 04:00 in-game time.",
            "Requires exactly 2 Cow Pet.",                 // tag already a pet-noun ⇒ no "pets" suffix
            "Requires the effect “Sandstorm”.",
            "Requires hands (not available in animal form).",
            "Werewolf characters only.",
            "Requires a guild hall.");
    }

    [Fact]
    public void PetCount_TreatsMinMaxAsBounds_NotARequiredQuantity()
    {
        var (nav, resolver) = Deps();
        // The only two shapes in the bundled corpus, plus a floor-only and a non-pet-noun.
        var reqs = new RecipeRequirement[]
        {
            new PetCountRecipeRequirement { T = "PetCount", PetTypeTag = "SummonedBakingBread", MaxCount = 4 },
            new PetCountRecipeRequirement { T = "PetCount", PetTypeTag = "StorageCrateDruid", MaxCount = 0 },
            new PetCountRecipeRequirement { T = "PetCount", PetTypeTag = "Wolf", MinCount = 2 },
        };

        var (lines, _) = RecipeRequirementProjector.Build(reqs, "Self", nav, resolver, NoStrings);

        lines.Should().Equal(
            "Requires at most 4 Summoned Baking Bread pets.",   // cap, not "requires 4"
            "Must not own any Storage Crate Druid.",            // Max 0 ⇒ disallowed, not "0 pets"
            "Requires at least 2 Wolf pets.");                  // "pet" appended (tag isn't a pet-noun)
    }

    [Fact]
    public void PetTypeTag_ResolvesThroughStringsAll_NotCamelSplit()
    {
        var (nav, resolver) = Deps();
        // PG models pet types as NPC/monster entities — the real in-game name lives in
        // strings_all under npc_<tag>_Name. Camel-splitting "SummonedBakingBread" to
        // "Summoned Baking Bread" fabricates a label the game never shows ("Rising Dough").
        var strings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["npc_SummonedBakingBread_Name"] = "Rising Dough",
            ["npc_StorageCrateDruid_Name"] = "Druidic Storage Crate",
        };
        var reqs = new RecipeRequirement[]
        {
            new PetCountRecipeRequirement { T = "PetCount", PetTypeTag = "SummonedBakingBread", MaxCount = 4 },
            new PetCountRecipeRequirement { T = "PetCount", PetTypeTag = "StorageCrateDruid", MaxCount = 0 },
        };

        var (lines, _) = RecipeRequirementProjector.Build(reqs, "Self", nav, resolver, strings);

        lines.Should().Equal(
            "Requires at most 4 Rising Dough pets.",
            "Must not own any Druidic Storage Crate.");
    }

    [Fact]
    public void RecipeKnown_AndCrossRecipeUsed_BecomeNavigableChips()
    {
        var (nav, resolver) = Deps();
        var reqs = new RecipeRequirement[]
        {
            new RecipeKnownRequirement { T = "RecipeKnown", Recipe = "MakeRoux" },
            new RecipeUsedRequirement { T = "RecipeUsed", Recipe = "OtherRecipe", MaxTimesUsed = 3 },
        };

        var (lines, chips) = RecipeRequirementProjector.Build(reqs, "MakeStew", nav, resolver, NoStrings);

        lines.Should().BeEmpty();
        chips.Should().HaveCount(2);
        chips[0].Reference!.Kind.Should().Be(EntityKind.Recipe);
        chips.Should().OnlyContain(c => c.IsNavigable);
    }

    [Fact]
    public void SelfReferentialRecipeUsed_IsACraftCapLine_NotADeadSelfChip()
    {
        var (nav, resolver) = Deps();
        var reqs = new RecipeRequirement[]
        {
            new RecipeUsedRequirement { T = "RecipeUsed", Recipe = "WeatherWitchRitual", MaxTimesUsed = 4 },
        };

        var (lines, chips) = RecipeRequirementProjector.Build(reqs, "WeatherWitchRitual", nav, resolver, NoStrings);

        chips.Should().BeEmpty();
        lines.Should().ContainSingle().Which.Should().Be("Limited to 5 crafts per character.");
    }

    [Fact]
    public void AlwaysFail_SaysItCanNeverBeCompleted()
    {
        var (nav, resolver) = Deps();
        var (lines, _) = RecipeRequirementProjector.Build(
            new RecipeRequirement[] { new AlwaysFailRequirement { T = "AlwaysFail" } }, "Self", nav, resolver, NoStrings);
        lines.Should().ContainSingle().Which.Should().Contain("never be completed");
    }

    [Fact]
    public void UnknownRequirement_DegradesGracefullyWithDiscriminator()
    {
        var (nav, resolver) = Deps();
        var (lines, _) = RecipeRequirementProjector.Build(
            new RecipeRequirement[] { new UnknownRecipeRequirement { T = "Brand_New", DiscriminatorValue = "Brand_New" } },
            "Self", nav, resolver, NoStrings);
        lines.Should().ContainSingle().Which.Should().Be("(unrecognised requirement: Brand_New)");
    }

    [Fact]
    public void RecipeChips_DegradeToNonNavigable_WhenRecipesTabAbsent()
    {
        var (nav, resolver) = Deps(recipesTabbed: false);
        var (_, chips) = RecipeRequirementProjector.Build(
            new RecipeRequirement[] { new RecipeKnownRequirement { T = "RecipeKnown", Recipe = "MakeRoux" } },
            "Self", nav, resolver, NoStrings);
        chips.Should().ContainSingle().Which.IsNavigable.Should().BeFalse();
    }
}
