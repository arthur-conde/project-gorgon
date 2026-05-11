using Arwen.Domain;
using FluentAssertions;
using Xunit;

namespace Arwen.Tests;

public sealed class ObservationFlagDetectorTests
{
    private static GiftObservation Obs(string npc, string item, double favor, double value, double pref, int quantity = 1, DateTimeOffset? at = null) =>
        new()
        {
            NpcKey = npc,
            ItemInternalName = item,
            ItemValue = value,
            FavorDelta = favor,
            Quantity = quantity,
            Timestamp = at ?? DateTimeOffset.UtcNow,
            MatchedPreferences = [new MatchedPreference { Name = "X", Pref = pref, Keywords = ["X"] }],
            ItemKeywords = [],
        };

    [Fact]
    public void Detect_FlagsOutlierWhenGroupHasMinSamples()
    {
        // Three "normal" observations with favor=20 each, one outlier at favor=120.
        // DerivedRate for normal = 20 / (100 * 1 * 1) = 0.2; outlier = 1.2.
        // Mean ≈ 0.45, σ ≈ 0.5 — outlier is ~1.5σ away, but with stdDevThreshold=1 it should flag.
        var observations = new List<GiftObservation>
        {
            Obs("NPC_A", "Item1", favor: 20, value: 100, pref: 1, at: DateTimeOffset.UnixEpoch.AddSeconds(1)),
            Obs("NPC_A", "Item1", favor: 20, value: 100, pref: 1, at: DateTimeOffset.UnixEpoch.AddSeconds(2)),
            Obs("NPC_A", "Item1", favor: 20, value: 100, pref: 1, at: DateTimeOffset.UnixEpoch.AddSeconds(3)),
            Obs("NPC_A", "Item1", favor: 120, value: 100, pref: 1, at: DateTimeOffset.UnixEpoch.AddSeconds(4)),
        };

        var flags = ObservationFlagDetector.Detect(observations, stdDevThreshold: 1.0, minGroupSize: 3);

        flags.Should().HaveCount(1, "only the high-favor observation is more than 1σ from the mean");
        var outlierKey = CalibrationService.ObservationKey(observations[3]);
        flags.Should().ContainKey(outlierKey);
        flags[outlierKey].Flag.Should().Be(ObservationFlag.OutlierInItemGroup);
        flags[outlierKey].GroupSampleCount.Should().Be(4);
        flags[outlierKey].SigmasFromMean.Should().BeGreaterThan(1.0);
    }

    [Fact]
    public void Detect_SkipsGroupsBelowMinSize()
    {
        // 2 observations, one a wild outlier — but below minGroupSize=3 → no flag.
        var observations = new List<GiftObservation>
        {
            Obs("NPC_A", "Item1", favor: 20, value: 100, pref: 1, at: DateTimeOffset.UnixEpoch.AddSeconds(1)),
            Obs("NPC_A", "Item1", favor: 500, value: 100, pref: 1, at: DateTimeOffset.UnixEpoch.AddSeconds(2)),
        };

        var flags = ObservationFlagDetector.Detect(observations, stdDevThreshold: 1.0, minGroupSize: 3);

        flags.Should().BeEmpty("two-sample groups have no statistical signal");
    }

    [Fact]
    public void Detect_HandlesZeroStdev()
    {
        // 3 observations with identical rates → stdev = 0 → no flag, no divide-by-zero.
        var observations = new List<GiftObservation>
        {
            Obs("NPC_A", "Item1", favor: 20, value: 100, pref: 1, at: DateTimeOffset.UnixEpoch.AddSeconds(1)),
            Obs("NPC_A", "Item1", favor: 20, value: 100, pref: 1, at: DateTimeOffset.UnixEpoch.AddSeconds(2)),
            Obs("NPC_A", "Item1", favor: 20, value: 100, pref: 1, at: DateTimeOffset.UnixEpoch.AddSeconds(3)),
        };

        var flags = ObservationFlagDetector.Detect(observations);

        flags.Should().BeEmpty("perfect agreement → no outliers");
    }

    [Fact]
    public void Detect_PartitionsGroupsByNpcAndItem()
    {
        // Same item to two different NPCs — the outlier-vs-baseline shouldn't bleed across.
        var observations = new List<GiftObservation>
        {
            // NPC_A: baseline rate ≈ 0.2
            Obs("NPC_A", "Item1", favor: 20, value: 100, pref: 1, at: DateTimeOffset.UnixEpoch.AddSeconds(1)),
            Obs("NPC_A", "Item1", favor: 20, value: 100, pref: 1, at: DateTimeOffset.UnixEpoch.AddSeconds(2)),
            Obs("NPC_A", "Item1", favor: 20, value: 100, pref: 1, at: DateTimeOffset.UnixEpoch.AddSeconds(3)),
            // NPC_B: baseline rate ≈ 1.0 — would look like outliers if grouped with NPC_A
            Obs("NPC_B", "Item1", favor: 100, value: 100, pref: 1, at: DateTimeOffset.UnixEpoch.AddSeconds(4)),
            Obs("NPC_B", "Item1", favor: 100, value: 100, pref: 1, at: DateTimeOffset.UnixEpoch.AddSeconds(5)),
            Obs("NPC_B", "Item1", favor: 100, value: 100, pref: 1, at: DateTimeOffset.UnixEpoch.AddSeconds(6)),
        };

        var flags = ObservationFlagDetector.Detect(observations);

        flags.Should().BeEmpty("each NPC group has zero variance internally");
    }

    [Fact]
    public void Detect_EmptyInputProducesEmptyResult()
    {
        ObservationFlagDetector.Detect([]).Should().BeEmpty();
    }
}
