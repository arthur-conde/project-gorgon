using Arda.Abstractions.Logs;
using Arda.World.Player.Events;
using FluentAssertions;
using Samwise.Parsing;
using Samwise.State;
using Xunit;

namespace Samwise.Tests;

/// <summary>
/// Regression tests for the <see cref="SkillUpdated"/> → <see cref="GardenStateMachine"/>
/// bridge. The Arda <see cref="SkillUpdated"/> event replaces the legacy
/// <c>IPlayerSkillState.SubscribeChanges</c> channel — Samwise filters to
/// Gardening-only and projects the harvest-confirmation <see cref="GardeningXp"/>.
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

    private static LogLineMetadata Meta(DateTime ts) =>
        new(new DateTimeOffset(ts, TimeSpan.Zero), DateTimeOffset.UtcNow, IsReplay: false);

    [Fact]
    public void Gardening_SkillUpdated_Projects_GardeningXp_With_Source_Timestamp()
    {
        var evt = new SkillUpdated(
            SkillKey: "Gardening",
            Raw: 50,
            Bonus: 0,
            Xp: 100,
            Tnl: 1000,
            Max: 200,
            XpGained: 42,
            Metadata: Meta(T));

        var projected = GardenIngestionService.TryProjectGardeningXp(evt);

        projected.Should().NotBeNull("a Gardening SkillUpdated is the harvest-confirmation signal");
        projected!.Timestamp.Should().Be(T,
            "the prior regex path produced GardeningXp(line.Timestamp); the bridge must preserve it");
    }

    [Fact]
    public void Non_Gardening_SkillUpdated_Does_Not_Project()
    {
        var evt = new SkillUpdated(
            SkillKey: "Survey",
            Raw: 50,
            Bonus: 0,
            Xp: 100,
            Tnl: 1000,
            Max: 200,
            XpGained: 42,
            Metadata: Meta(T));

        GardenIngestionService.TryProjectGardeningXp(evt).Should().BeNull();
    }

    [Fact]
    public void Gardening_SkillUpdated_Is_Case_Sensitive_On_SkillKey()
    {
        var evt = new SkillUpdated(
            SkillKey: "gardening",
            Raw: 50,
            Bonus: 0,
            Xp: 100,
            Tnl: 1000,
            Max: 200,
            XpGained: 1,
            Metadata: Meta(T));

        GardenIngestionService.TryProjectGardeningXp(evt).Should().BeNull();
    }
}
