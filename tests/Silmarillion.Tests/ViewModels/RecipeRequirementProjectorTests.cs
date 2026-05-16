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
/// user-asserted contract is unverifiable. Rows are the Quest dual-shape: prose, or a
/// "{Prefix} [chip]" sentence with an inline navigable chip.
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

    private static IReadOnlyList<string> Texts(IEnumerable<RecipeRequirementRow> rows) =>
        rows.Select(r => r.Text).ToList();

    [Fact]
    public void EmptyOrNull_YieldsNoRows()
    {
        var (nav, resolver) = Deps();
        RecipeRequirementProjector.Build(null, "Self", nav, resolver, NoStrings).Should().BeEmpty();
    }

    [Fact]
    public void CyclicalAndUserAssertedGates_RenderAsProseRows()
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

        var rows = RecipeRequirementProjector.Build(reqs, "Self", nav, resolver, NoStrings);

        rows.Should().OnlyContain(r => r.Chip == null && r.Prefix == null);
        Texts(rows).Should().Equal(
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
    public void ProseAndChipRows_StayInterleavedInAuthoredOrder()
    {
        // The whole point of the row model: a cross-link chip reads in the same flow as
        // the prose gates around it, not as a trailing orphaned cluster.
        var (nav, resolver) = Deps();
        var reqs = new RecipeRequirement[]
        {
            new FullMoonRecipeRequirement { T = "FullMoon" },
            new RecipeKnownRequirement { T = "RecipeKnown", Recipe = "Augury1" },
            new HasGuildHallRequirement { T = "HasGuildHall" },
        };

        var rows = RecipeRequirementProjector.Build(reqs, "Self", nav, resolver, NoStrings);

        rows.Should().HaveCount(3);
        rows[0].Chip.Should().BeNull();
        rows[0].Text.Should().Be("Only during the full moon.");
        rows[1].Chip.Should().NotBeNull();                         // chip sits *between* prose rows
        rows[1].Prefix.Should().Be("Requires recipe:");
        rows[2].Chip.Should().BeNull();
        rows[2].Text.Should().Be("Requires a guild hall.");
    }

    [Fact]
    public void PetCount_TreatsMinMaxAsBounds_NotARequiredQuantity()
    {
        var (nav, resolver) = Deps();
        var reqs = new RecipeRequirement[]
        {
            new PetCountRecipeRequirement { T = "PetCount", PetTypeTag = "SummonedBakingBread", MaxCount = 4 },
            new PetCountRecipeRequirement { T = "PetCount", PetTypeTag = "StorageCrateDruid", MaxCount = 0 },
            new PetCountRecipeRequirement { T = "PetCount", PetTypeTag = "Wolf", MinCount = 2 },
        };

        var rows = RecipeRequirementProjector.Build(reqs, "Self", nav, resolver, NoStrings);

        Texts(rows).Should().Equal(
            "Requires at most 4 Summoned Baking Bread pets.",   // cap, not "requires 4"
            "Must not own any Storage Crate Druid.",            // Max 0 ⇒ disallowed, not "0 pets"
            "Requires at least 2 Wolf pets.");                  // "pet" appended (tag isn't a pet-noun)
    }

    [Fact]
    public void PetTypeTag_ResolvesThroughStringsAll_NotCamelSplit()
    {
        var (nav, resolver) = Deps();
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

        var rows = RecipeRequirementProjector.Build(reqs, "Self", nav, resolver, strings);

        Texts(rows).Should().Equal(
            "Requires at most 4 Rising Dough pets.",
            "Must not own any Druidic Storage Crate.");
    }

    [Fact]
    public void RecipeKnown_AndCrossRecipeUsed_BecomeNavigableChipRows_WithPrefixes()
    {
        var (nav, resolver) = Deps();
        var reqs = new RecipeRequirement[]
        {
            new RecipeKnownRequirement { T = "RecipeKnown", Recipe = "MakeRoux" },
            new RecipeUsedRequirement { T = "RecipeUsed", Recipe = "OtherRecipe", MaxTimesUsed = 3 },
        };

        var rows = RecipeRequirementProjector.Build(reqs, "MakeStew", nav, resolver, NoStrings);

        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.Chip != null && r.Chip!.IsNavigable
                                       && r.Chip.Reference!.Kind == EntityKind.Recipe);
        rows[0].Prefix.Should().Be("Requires recipe:");
        rows[0].Text.Should().Be("Requires recipe: MakeRoux");          // prose fallback = prefix + name
        rows[1].Prefix.Should().Be("Requires having crafted:");
    }

    [Fact]
    public void SelfReferentialRecipeUsed_IsACraftCapProseRow_NotADeadSelfChip()
    {
        var (nav, resolver) = Deps();
        var reqs = new RecipeRequirement[]
        {
            new RecipeUsedRequirement { T = "RecipeUsed", Recipe = "WeatherWitchRitual", MaxTimesUsed = 4 },
        };

        var rows = RecipeRequirementProjector.Build(reqs, "WeatherWitchRitual", nav, resolver, NoStrings);

        rows.Should().ContainSingle().Which.Chip.Should().BeNull();
        rows.Single().Text.Should().Be("Limited to 5 crafts per character.");
    }

    [Fact]
    public void AlwaysFail_SaysItCanNeverBeCompleted()
    {
        var (nav, resolver) = Deps();
        var rows = RecipeRequirementProjector.Build(
            new RecipeRequirement[] { new AlwaysFailRequirement { T = "AlwaysFail" } }, "Self", nav, resolver, NoStrings);
        rows.Should().ContainSingle().Which.Text.Should().Contain("never be completed");
    }

    [Fact]
    public void UnknownRequirement_DegradesGracefullyWithDiscriminator()
    {
        var (nav, resolver) = Deps();
        var rows = RecipeRequirementProjector.Build(
            new RecipeRequirement[] { new UnknownRecipeRequirement { T = "Brand_New", DiscriminatorValue = "Brand_New" } },
            "Self", nav, resolver, NoStrings);
        rows.Should().ContainSingle().Which.Text.Should().Be("(unrecognised requirement: Brand_New)");
    }

    [Fact]
    public void ChipRow_DegradesToNonNavigable_WhenRecipesTabAbsent()
    {
        var (nav, resolver) = Deps(recipesTabbed: false);
        var rows = RecipeRequirementProjector.Build(
            new RecipeRequirement[] { new RecipeKnownRequirement { T = "RecipeKnown", Recipe = "MakeRoux" } },
            "Self", nav, resolver, NoStrings);
        rows.Should().ContainSingle().Which.Chip!.IsNavigable.Should().BeFalse();
    }
}
