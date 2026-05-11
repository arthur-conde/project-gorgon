using System.Text.Json;
using FluentAssertions;
using Samwise.Alarms;
using Samwise.State;
using Xunit;

namespace Samwise.Tests.Alarms;

public class AlarmSettingsTests
{
    [Fact]
    public void NewRule_LoopDefaultsToFalse()
    {
        new StageAlarmRule().Loop.Should().BeFalse();
    }

    [Fact]
    public void NewRule_ChannelIdDefaultsToDefault()
    {
        new StageAlarmRule().ChannelId.Should().Be("default");
    }

    [Fact]
    public void StageAlarmRule_JsonRoundTrip_PreservesLoopAndChannel()
    {
        var settings = new SamwiseSettings();
        settings.Alarms.Rules[PlotStage.Ripe].Loop = true;
        settings.Alarms.Rules[PlotStage.Ripe].ChannelId = "custom-channel-id";

        var json = JsonSerializer.Serialize(settings, SamwiseSettingsJsonContext.Default.SamwiseSettings);
        var deserialized = JsonSerializer.Deserialize(json, SamwiseSettingsJsonContext.Default.SamwiseSettings)!;

        var ripe = deserialized.Alarms.Rules[PlotStage.Ripe];
        ripe.Loop.Should().BeTrue();
        ripe.ChannelId.Should().Be("custom-channel-id");
    }

    [Fact]
    public void NewSettings_HasOneDefaultChannelInReplaceMode()
    {
        var s = new SamwiseSettings();
        s.Alarms.Channels.Should().HaveCount(1);
        s.Alarms.Channels[0].Id.Should().Be("default");
        s.Alarms.Channels[0].Name.Should().Be("Default");
        s.Alarms.Channels[0].Collision.Should().Be(AlarmCollisionBehavior.Replace);
    }

    [Fact]
    public void NewSettings_AllRulesRouteToDefaultChannel()
    {
        var s = new SamwiseSettings();
        foreach (var rule in s.Alarms.Rules.Values)
            rule.ChannelId.Should().Be("default");
    }

    [Fact]
    public void PostLoadInit_OldJsonWithoutChannels_InjectsDefaultChannel()
    {
        // Simulates old user JSON that predates the Channels property.
        const string oldJson = """
            {
              "alarms": {
                "enabled": true,
                "rules": {
                  "Ripe": { "enabled": true, "soundFilePath": null, "stopOnInteraction": true }
                }
              },
              "harvestedAutoClearMinutes": 10
            }
            """;

        var loaded = JsonSerializer.Deserialize(oldJson, SamwiseSettingsJsonContext.Default.SamwiseSettings)!;
        (loaded as Mithril.Shared.Settings.IPostLoadInit)?.PostLoadInit();

        loaded.Alarms.Channels.Should().NotBeEmpty();
        loaded.Alarms.Channels[0].Id.Should().Be("default");
        loaded.Alarms.Rules[PlotStage.Ripe].ChannelId.Should().Be("default");
    }

    [Fact]
    public void PostLoadInit_RuleWithDanglingChannelId_ReassignsToFirstChannel()
    {
        var s = new SamwiseSettings();
        s.Alarms.Rules[PlotStage.Ripe].ChannelId = "nonexistent-channel";

        (s as Mithril.Shared.Settings.IPostLoadInit)?.PostLoadInit();

        s.Alarms.Rules[PlotStage.Ripe].ChannelId.Should().Be(s.Alarms.Channels[0].Id);
    }

    [Fact]
    public void Channels_JsonRoundTrip_PreservesChannelId()
    {
        var s = new SamwiseSettings();
        // Add a second channel with a non-default Id to make this stronger
        // than just verifying the literal "default" string survives.
        var extra = new AlarmChannel { Name = "Extra", Collision = AlarmCollisionBehavior.Suppress };
        var extraId = extra.Id;
        s.Alarms.Channels.Add(extra);

        var json = JsonSerializer.Serialize(s, SamwiseSettingsJsonContext.Default.SamwiseSettings);
        var loaded = JsonSerializer.Deserialize(json, SamwiseSettingsJsonContext.Default.SamwiseSettings)!;
        (loaded as Mithril.Shared.Settings.IPostLoadInit)?.PostLoadInit();

        loaded.Alarms.Channels.Should().HaveCount(2);
        loaded.Alarms.Channels[0].Id.Should().Be("default");
        loaded.Alarms.Channels[1].Id.Should().Be(extraId);
        loaded.Alarms.Channels[1].Name.Should().Be("Extra");
        loaded.Alarms.Channels[1].Collision.Should().Be(AlarmCollisionBehavior.Suppress);
    }

    [Fact]
    public void MembershipSummary_AfterPostLoadInit_ListsAssignedStages()
    {
        var s = new SamwiseSettings();
        (s as Mithril.Shared.Settings.IPostLoadInit)?.PostLoadInit();

        s.Alarms.Channels[0].MembershipSummary.Should().Contain("Ripe");
        s.Alarms.Channels[0].MembershipSummary.Should().Contain("Thirsty");
        s.Alarms.Channels[0].MembershipSummary.Should().Contain("NeedsFertilizer");
    }

    [Fact]
    public void MembershipSummary_RecomputesWhenRuleChannelIdChanges()
    {
        var s = new SamwiseSettings();
        var extra = new AlarmChannel { Name = "Extra", Collision = AlarmCollisionBehavior.Suppress };
        s.Alarms.Channels.Add(extra);
        (s as Mithril.Shared.Settings.IPostLoadInit)?.PostLoadInit();

        s.Alarms.Rules[PlotStage.Ripe].ChannelId = extra.Id;

        extra.MembershipSummary.Should().Contain("Ripe");
        s.Alarms.Channels[0].MembershipSummary.Should().NotContain("Ripe");
    }
}
