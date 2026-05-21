using FluentAssertions;
using Mithril.GameState.Skills;
using Samwise.Parsing;
using Samwise.State;
using Xunit;

namespace Samwise.Tests;

/// <summary>
/// Regression tests for the <see cref="IPlayerSkillState.SubscribeChanges"/>
/// → <see cref="GardenStateMachine"/> bridge introduced in
/// <a href="https://github.com/moumantai-gg/mithril/issues/581">#581</a>
/// (Class A migration from #579 — Samwise stops re-parsing
/// <c>ProcessUpdateSkill.*type=Gardening</c> and consumes the canonical
/// skill-state service instead).
///
/// <para>The bridge is exercised through
/// <see cref="GardenIngestionService.TryProjectGardeningXp"/>, the pure
/// decision helper. The dispatch wrapper above it adds only diagnostics
/// and a UI-thread hop, so the filter contract is the load-bearing piece
/// worth pinning here.</para>
/// </summary>
public class GardeningXpSkillStateBridgeTests
{
    private static readonly DateTime T = new(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

    private static SkillProgressSnapshot AnySnapshot(int level = 50, long xp = 100) =>
        new(Level: level, BonusLevels: 0, XpTowardNextLevel: xp, XpNeededForNextLevel: 1000, MaxLevel: 200);

    [Fact]
    public void Gardening_Delta_Projects_GardeningXp_With_Source_Timestamp()
    {
        var change = new SkillChange(
            SkillKey: "Gardening",
            Previous: AnySnapshot(level: 49),
            Current: AnySnapshot(level: 50),
            XpGained: 42,
            Kind: SkillChangeKind.Delta,
            Timestamp: T);

        var projected = GardenIngestionService.TryProjectGardeningXp(change);

        projected.Should().NotBeNull("a Gardening Delta is the harvest-confirmation signal");
        projected!.Timestamp.Should().Be(T,
            "the prior regex path produced GardeningXp(line.Timestamp); the bridge must preserve it");
    }

    [Fact]
    public void Non_Gardening_Delta_Does_Not_Project()
    {
        // Every other skill's delta is unrelated to garden harvest commits.
        var change = new SkillChange(
            SkillKey: "Survey",
            Previous: AnySnapshot(level: 49),
            Current: AnySnapshot(level: 50),
            XpGained: 42,
            Kind: SkillChangeKind.Delta,
            Timestamp: T);

        GardenIngestionService.TryProjectGardeningXp(change).Should().BeNull();
    }

    [Fact]
    public void Gardening_SnapshotReplace_Does_Not_Project()
    {
        // The pre-#581 regex only matched ProcessUpdateSkill, not the
        // periodic ProcessLoadSkills re-sync. A snapshot reconcile must
        // NOT commit a harvest — it would re-fire HandleGardeningXp on
        // every zone change and burn pending-harvest discriminator state.
        var change = new SkillChange(
            SkillKey: "Gardening",
            Previous: AnySnapshot(level: 49),
            Current: AnySnapshot(level: 50),
            XpGained: 0,
            Kind: SkillChangeKind.SnapshotReplace,
            Timestamp: T);

        GardenIngestionService.TryProjectGardeningXp(change).Should().BeNull();
    }

    [Fact]
    public void Other_Skill_SnapshotReplace_Does_Not_Project()
    {
        // Both gates fail; assert combined for completeness.
        var change = new SkillChange(
            SkillKey: "FireMagic",
            Previous: null,
            Current: AnySnapshot(),
            XpGained: 0,
            Kind: SkillChangeKind.SnapshotReplace,
            Timestamp: T);

        GardenIngestionService.TryProjectGardeningXp(change).Should().BeNull();
    }

    [Fact]
    public void Gardening_Delta_Is_Case_Sensitive_On_SkillKey()
    {
        // SkillKey is the internal-name key (project-wide convention) —
        // Ordinal compare. Mis-cased input should not project (the
        // canonical IPlayerSkillState would never emit a mis-cased key,
        // but we pin the strict-equality contract here so a future
        // refactor doesn't silently loosen it).
        var change = new SkillChange(
            SkillKey: "gardening",
            Previous: null,
            Current: AnySnapshot(),
            XpGained: 1,
            Kind: SkillChangeKind.Delta,
            Timestamp: T);

        GardenIngestionService.TryProjectGardeningXp(change).Should().BeNull();
    }
}
