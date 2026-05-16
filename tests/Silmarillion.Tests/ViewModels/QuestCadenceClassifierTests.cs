using FluentAssertions;
using Mithril.Reference.Models.Quests;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.ViewModels;

public sealed class QuestCadenceClassifierTests
{
    private static Quest Q(int? days = null, int? hours = null, int? minutes = null) =>
        new() { ReuseTime_Days = days, ReuseTime_Hours = hours, ReuseTime_Minutes = minutes };

    [Fact]
    public void NoReuseFields_IsOneTime()
    {
        QuestCadenceClassifier.Classify(Q()).Should().Be(QuestCadence.OneTime);
        QuestCadenceClassifier.ReuseMinutes(Q()).Should().Be(0);
        QuestCadenceClassifier.BadgeText(Q()).Should().BeNull();
    }

    [Theory]
    [InlineData(20)] // PG's actual daily cooldown — the case that started #345
    [InlineData(21)]
    [InlineData(24)] // top of the band
    public void TwentyToTwentyFourHours_IsDaily(int hours)
    {
        QuestCadenceClassifier.Classify(Q(hours: hours)).Should().Be(QuestCadence.Daily);
        QuestCadenceClassifier.BadgeText(Q(hours: hours)).Should().Be("Daily");
    }

    [Theory]
    [InlineData(19)]  // just below the band
    [InlineData(12)]
    public void HoursBelowDailyBand_IsOther(int hours)
    {
        QuestCadenceClassifier.Classify(Q(hours: hours)).Should().Be(QuestCadence.Other);
        QuestCadenceClassifier.BadgeText(Q(hours: hours)).Should().Be($"Every {hours}h");
    }

    [Fact]
    public void TwentyHoursPlusMinutes_FallsOutOfDailyBand()
    {
        // 20h is daily; 20h30m is not — minutes must be zero for the friendly bucket.
        var q = Q(hours: 20, minutes: 30);
        QuestCadenceClassifier.Classify(q).Should().Be(QuestCadence.Other);
        QuestCadenceClassifier.BadgeText(q).Should().Be("Every 20h 30m");
        QuestCadenceClassifier.ReuseMinutes(q).Should().Be(20 * 60 + 30);
    }

    [Fact]
    public void TwentyFiveHours_IsOther()
    {
        QuestCadenceClassifier.Classify(Q(hours: 25)).Should().Be(QuestCadence.Other);
    }

    [Fact]
    public void ExactlySevenDays_IsWeekly()
    {
        QuestCadenceClassifier.Classify(Q(days: 7)).Should().Be(QuestCadence.Weekly);
        QuestCadenceClassifier.BadgeText(Q(days: 7)).Should().Be("Weekly");
        QuestCadenceClassifier.ReuseMinutes(Q(days: 7)).Should().Be(7 * 24 * 60);
    }

    [Theory]
    [InlineData(7, 0, 1)]   // 7d1m — not exactly a week
    [InlineData(6, 23, 0)]  // just under a week
    [InlineData(14, 0, 0)]  // fortnight
    public void NearWeekButNotExact_IsOther(int days, int hours, int minutes)
    {
        QuestCadenceClassifier.Classify(Q(days, hours, minutes)).Should().Be(QuestCadence.Other);
    }

    [Fact]
    public void ReuseMinutes_SumsAllThreeFields()
    {
        QuestCadenceClassifier.ReuseMinutes(Q(days: 1, hours: 2, minutes: 3))
            .Should().Be((1 * 24 * 60) + (2 * 60) + 3);
    }

    [Fact]
    public void Other_BadgeText_OmitsZeroParts()
    {
        QuestCadenceClassifier.BadgeText(Q(days: 3)).Should().Be("Every 3d");
        QuestCadenceClassifier.BadgeText(Q(minutes: 30)).Should().Be("Every 30m");
        QuestCadenceClassifier.BadgeText(Q(days: 2, minutes: 15)).Should().Be("Every 2d 15m");
    }

    [Fact]
    public void DetailText_RecoversPrecisionBehindFriendlyBadge()
    {
        // The case from #345: badge says "Daily", tooltip says the real 20h.
        QuestCadenceClassifier.BadgeText(Q(hours: 20)).Should().Be("Daily");
        QuestCadenceClassifier.DetailText(Q(hours: 20)).Should().Be("Repeatable every 20 hours");

        QuestCadenceClassifier.DetailText(Q(days: 7)).Should().Be("Repeatable every 7 days");
        QuestCadenceClassifier.DetailText(Q(days: 1, hours: 1, minutes: 1))
            .Should().Be("Repeatable every 1 day 1 hour 1 minute");
    }

    [Fact]
    public void DetailText_NullForOneTime()
    {
        QuestCadenceClassifier.DetailText(Q()).Should().BeNull();
    }
}
