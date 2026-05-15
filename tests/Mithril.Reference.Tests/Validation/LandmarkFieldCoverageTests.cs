using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Mithril.Reference.Models.Misc;
using Mithril.Reference.Serialization;
using Xunit;

namespace Mithril.Reference.Tests.Validation;

/// <summary>
/// Regression net for <see cref="Landmark"/>'s JSON-binding: confirms the bundled
/// <c>landmarks.json</c> populates every modelled property on at least one entry
/// (and that <see cref="Landmark.Type"/> is universally present, <see cref="Landmark.Combo"/>
/// is present on every <c>MeditationPillar</c>). Catches a future POCO refactor that
/// renames a property and silently drops the Newtonsoft binding — the canonical
/// "POCO field coverage" hazard called out in the silmarillion tab cookbook.
/// </summary>
public class LandmarkFieldCoverageTests
{
    private static IReadOnlyDictionary<string, IReadOnlyList<Landmark>> LoadBundled()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "BundledData", "landmarks.json");
        File.Exists(path).Should().BeTrue($"Expected bundled fixture at {path}.");
        return ReferenceDeserializer.ParseLandmarks(File.ReadAllText(path));
    }

    [Fact]
    public void Type_Is_Populated_On_Every_Landmark()
    {
        var all = LoadBundled().Values.SelectMany(v => v).ToList();

        all.Should().NotBeEmpty();
        all.Should().OnlyContain(l => !string.IsNullOrEmpty(l.Type),
            "every bundled landmark carries a Type field; an unset value indicates the POCO property no longer binds to the JSON.");
    }

    [Fact]
    public void Type_Values_Are_Drawn_From_Known_Set()
    {
        var types = LoadBundled().Values
            .SelectMany(v => v)
            .Select(l => l.Type)
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct()
            .ToList();

        types.Should().BeSubsetOf(new[] { "Portal", "MeditationPillar", "TeleportationPlatform" },
            "the bundled corpus uses exactly these three Type values; an unexpected value should surface as a test failure so the per-type rendering decision can be revisited.");
    }

    [Fact]
    public void Combo_Is_Populated_On_Every_MeditationPillar()
    {
        var pillars = LoadBundled().Values
            .SelectMany(v => v)
            .Where(l => l.Type == "MeditationPillar")
            .ToList();

        pillars.Should().NotBeEmpty("the bundled corpus should contain at least one MeditationPillar.");
        pillars.Should().OnlyContain(l => !string.IsNullOrEmpty(l.Combo),
            "every MeditationPillar carries a Combo field; an unset value indicates the POCO property no longer binds to the JSON.");
    }

    [Fact]
    public void Combo_Is_Null_On_NonPillar_Landmarks()
    {
        var nonPillars = LoadBundled().Values
            .SelectMany(v => v)
            .Where(l => l.Type != "MeditationPillar")
            .ToList();

        nonPillars.Should().NotBeEmpty();
        nonPillars.Should().OnlyContain(l => string.IsNullOrEmpty(l.Combo),
            "Combo is meditation-pillar-specific; portals and teleportation platforms should leave it unset.");
    }
}
