using FluentAssertions;
using Mithril.Reference.Models.Quests;
using Mithril.Shared.Wpf.Query;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.ViewModels;

/// <summary>
/// End-to-end check of the exact runtime query path the Quests tab uses:
/// <c>ColumnBindingHelper.BuildFromProperties(typeof(QuestListRow))</c> +
/// <c>QueryCompiler.Compile(string, …)</c> (which runs <see cref="QueryParser"/>).
/// Guards the #345 promise that the friendly "Daily" badge and the queryable
/// <see cref="QuestListRow.Cadence"/> column agree for the canonical 20h quest.
/// </summary>
public sealed class QuestCadenceQueryIntegrationTests
{
    // Mirrors QuestsTabViewModel.BuildRow for the fields under test (KilltheDenMotherRepeat
    // ships ReuseTime_Hours = 20 in the bundled data).
    private static QuestListRow DenMotherRow()
    {
        var quest = new Quest
        {
            InternalName = "KilltheDenMotherRepeat",
            Name = "Kill the Ranalon Den Mother",
            ReuseTime_Hours = 20,
        };
        var cadence = QuestCadenceClassifier.Classify(quest);
        return new QuestListRow(
            Quest: quest,
            InternalName: quest.InternalName!,
            Name: quest.Name!,
            Level: null,
            FavorNpcDisplayName: null,
            DisplayedLocation: null,
            Keywords: [],
            IsCancellable: true,
            IsGuildQuest: false,
            IsWorkOrder: false,
            IsRepeatable: cadence != QuestCadence.OneTime,
            Cadence: cadence,
            ReuseMinutes: QuestCadenceClassifier.ReuseMinutes(quest));
    }

    private static bool Matches(string query)
    {
        var columns = ColumnBindingHelper.BuildFromProperties(typeof(QuestListRow));
        var predicate = QueryCompiler.Compile(query, columns);
        predicate.Should().NotBeNull($"'{query}' should parse as grammar");
        return predicate!(DenMotherRow());
    }

    [Fact]
    public void BadgeSaysDaily_ForTheRow()
    {
        QuestCadenceClassifier.BadgeText(DenMotherRow().Quest).Should().Be("Daily");
    }

    [Theory]
    [InlineData("Cadence = \"Daily\"")]
    [InlineData("Cadence = \"daily\"")]          // case-insensitive enum parse
    [InlineData("Cadence != \"Weekly\"")]
    [InlineData("ReuseMinutes = 1200")]
    [InlineData("ReuseMinutes >= 720")]
    [InlineData("Cadence = \"Daily\" AND ReuseMinutes > 60")]
    public void DenMother_MatchesDailyQueries(string query)
    {
        Matches(query).Should().BeTrue($"the 20h Den Mother quest should satisfy: {query}");
    }

    [Theory]
    [InlineData("Cadence = \"Weekly\"")]
    [InlineData("Cadence = \"OneTime\"")]
    [InlineData("ReuseMinutes <= 720")]
    public void DenMother_DoesNotMatchNonDailyQueries(string query)
    {
        Matches(query).Should().BeFalse($"the 20h Den Mother quest should NOT satisfy: {query}");
    }
}
