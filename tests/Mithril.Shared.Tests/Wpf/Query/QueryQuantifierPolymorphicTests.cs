using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Quests;
using Mithril.Shared.Wpf.Query;
using Xunit;

namespace Mithril.Shared.Tests.Wpf.Query;

/// <summary>
/// <c>WITH ANY|ALL</c> over a <em>polymorphic</em> element collection (slice 2c).
/// Uses the real reference hierarchies: <see cref="QuestRequirement"/> (Mandatory —
/// <c>Level</c> is <c>string?</c>/<c>int?</c> across subtypes) and
/// <see cref="NpcService"/> (Optional — no collisions, single-subtype soft warning).
/// The points proven: per-element narrowing by discriminator guard, a
/// sibling-subtype property reads as absent (no match, no throw), mandatory
/// enforcement, and the optional soft warning.
/// </summary>
public class QueryQuantifierPolymorphicTests
{
    private sealed record QHolder(string Name, IReadOnlyList<QuestRequirement>? Reqs);

    private static readonly QHolder[] Quests =
    {
        // A skill-level row (Level is string "Friends") + a combat row (Level is int 10).
        new("mixed",
        [
            new MinSkillLevelRequirement { T = "MinSkillLevel", Skill = "Cooking", Level = "Friends" },
            new MinCombatSkillLevelRequirement { T = "MinCombatSkillLevel", Level = 10 },
        ]),
        // Only a combat row, Level int 3.
        new("combatOnly",
        [
            new MinCombatSkillLevelRequirement { T = "MinCombatSkillLevel", Level = 3 },
        ]),
        new("none", []),
        new("nullreqs", null),
    };

    private static readonly IReadOnlyDictionary<string, ColumnBinding> QuestCols =
        new Dictionary<string, ColumnBinding>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = new("name", typeof(string), r => ((QHolder)r).Name),
            ["reqs"] = new("reqs", typeof(IReadOnlyList<QuestRequirement>), r => ((QHolder)r).Reqs),
        };

    private static string[] FilterQuests(string query, ICollection<QueryDiagnostic>? w = null)
    {
        var predicate = QueryCompiler.Compile(query, QuestCols, w);
        return predicate is null
            ? Quests.Select(h => h.Name).ToArray()
            : Quests.Where(h => predicate(h)).Select(h => h.Name).ToArray();
    }

    // ─────────────── per-element narrowing + absent ───────────────

    [Fact]
    public void Guarded_int_path_compiles_against_the_combat_subtype()
    {
        // Level > 5 is only valid because the T='MinCombatSkillLevel' guard resolves
        // Level to int? in this scope. "mixed" has a combat row Level=10; "combatOnly"
        // has Level=3 (fails > 5).
        FilterQuests("reqs WITH ANY (T = 'MinCombatSkillLevel' AND Level > 5)")
            .Should().BeEquivalentTo("mixed");
    }

    [Fact]
    public void Guarded_string_path_compiles_against_the_skill_subtype()
    {
        FilterQuests("reqs WITH ANY (T = 'MinSkillLevel' AND Level = 'Friends')")
            .Should().BeEquivalentTo("mixed");
    }

    [Fact]
    public void Sibling_subtype_property_reads_as_absent_no_match_no_throw()
    {
        // Skill exists only on MinSkillLevelRequirement. The guard scopes it; the
        // combat element has no Skill → absent → that element just doesn't match.
        FilterQuests("reqs WITH ANY (T = 'MinSkillLevel' AND Skill = 'Cooking')")
            .Should().BeEquivalentTo("mixed");
        FilterQuests("reqs WITH ANY (T = 'MinSkillLevel' AND Skill = 'Nope')")
            .Should().BeEmpty();
    }

    // ─────────────── mandatory enforcement ───────────────

    [Fact]
    public void Mandatory_colliding_prop_without_guard_throws()
    {
        Action act = () => QueryCompiler.Compile("reqs WITH ANY (Level > 5)", QuestCols);
        act.Should().Throw<QueryException>().WithMessage("*Level*");
    }

    [Fact]
    public void Mandatory_subtype_specific_prop_without_guard_throws()
    {
        // AllowSkill is declared on exactly one subtype; in a Mandatory hierarchy a
        // subtype-specific reference still needs a discriminator guard.
        Action act = () => QueryCompiler.Compile("reqs WITH ANY (AllowSkill = 'Sword')", QuestCols);
        act.Should().Throw<QueryException>();
    }

    [Fact]
    public void Not_equals_does_not_count_as_a_guard()
    {
        Action act = () => QueryCompiler.Compile(
            "reqs WITH ANY (T != 'MinSkillLevel' AND Level = 'x')", QuestCols);
        act.Should().Throw<QueryException>();
    }

    [Fact]
    public void OR_arm_guard_does_not_scope_the_sibling_arm()
    {
        // First arm is guarded; second arm references the colliding Level with no
        // guard of its own → still a mandatory violation.
        Action act = () => QueryCompiler.Compile(
            "reqs WITH ANY ((T = 'MinSkillLevel' AND Level = 'a') OR (Level = 'b'))", QuestCols);
        act.Should().Throw<QueryException>();
    }

    [Fact]
    public void Divergent_multi_scope_colliding_type_is_a_v1_limitation_throw()
    {
        // Level resolves to string? in one OR-branch and int? in the other; the
        // inner predicate compiles once so v1 rejects it with guidance.
        Action act = () => QueryCompiler.Compile(
            "reqs WITH ANY ((T = 'MinCombatSkillLevel' AND Level > 5) " +
            "OR (T = 'MinSkillLevel' AND Level = 'x'))", QuestCols);
        act.Should().Throw<QueryException>().WithMessage("*multiple types*");
    }

    // ─────────────── optional soft warning (NpcService) ───────────────

    private sealed record SHolder(string Name, IReadOnlyList<NpcService>? Services);

    private static readonly SHolder[] Npcs =
    {
        new("store",
        [
            new StoreService { Type = "Store", CapIncreases = ["Despised:5000:Armor"] },
            new AnimalHusbandryService { Type = "AnimalHusbandry" },
        ]),
        new("husbandryOnly",
        [
            new AnimalHusbandryService { Type = "AnimalHusbandry" },
        ]),
    };

    private static readonly IReadOnlyDictionary<string, ColumnBinding> NpcCols =
        new Dictionary<string, ColumnBinding>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = new("name", typeof(string), r => ((SHolder)r).Name),
            ["services"] = new("services", typeof(IReadOnlyList<NpcService>), r => ((SHolder)r).Services),
        };

    [Fact]
    public void Optional_single_subtype_prop_without_guard_warns_but_works()
    {
        var warnings = new List<QueryDiagnostic>();
        var predicate = QueryCompiler.Compile(
            "services WITH ANY (CapIncreases CONTAINS 'Despised:5000:Armor')", NpcCols, warnings);

        predicate.Should().NotBeNull();
        // The husbandry element in "store" has no CapIncreases → absent → skipped;
        // the StoreService element matches → "store" passes; "husbandryOnly" doesn't.
        Npcs.Where(h => predicate!(h)).Select(h => h.Name)
            .Should().BeEquivalentTo("store");
        warnings.Should().ContainSingle()
            .Which.Message.Should().Contain("CapIncreases");
    }

    [Fact]
    public void Optional_single_subtype_prop_with_guard_does_not_warn()
    {
        var warnings = new List<QueryDiagnostic>();
        var predicate = QueryCompiler.Compile(
            "services WITH ANY (Type = 'Store' AND CapIncreases CONTAINS 'Despised:5000:Armor')",
            NpcCols, warnings);

        Npcs.Where(h => predicate!(h)).Select(h => h.Name)
            .Should().BeEquivalentTo("store");
        warnings.Should().BeEmpty();
    }
}
