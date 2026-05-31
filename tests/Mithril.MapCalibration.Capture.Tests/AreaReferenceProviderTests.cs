using FluentAssertions;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.Reference.Models.Misc;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// Task 22 (#914): builds the area's landmark/NPC reference points for the
/// solver. The <see cref="Landmark.Type"/> strings + <see cref="Landmark.Loc"/>
/// format are confirmed against live landmarks.json (v470):
/// Type ∈ { Portal, MeditationPillar, TeleportationPlatform }; Loc = "x:N y:N z:N".
/// </summary>
public sealed class AreaReferenceProviderTests
{
    [Fact]
    public void Maps_landmarks_and_npcs_to_typed_references()
    {
        var refData = new FakeAreaReferenceData()
            .WithLandmarks("AreaSerbule",
                new Landmark { Name = "Serbule Portal", Loc = "x:10 y:0 z:-20", Type = "Portal" },
                new Landmark { Name = "Medi Pillar", Loc = "x:5 y:0 z:5", Type = "MeditationPillar" },
                new Landmark { Name = "Teleport Circle", Loc = "x:1 y:0 z:2", Type = "TeleportationPlatform" })
            .WithNpc("AreaSerbule", "Marna", pos: "x:30 y:0 z:40");

        var refs = new ReferenceDataAreaReferenceProvider(refData).ForArea("AreaSerbule");

        refs.Should().Contain(r => r.Type == "landmark_portal" && r.World.X == 10 && r.World.Z == -20);
        refs.Should().Contain(r => r.Type == "landmark_medipillar");
        refs.Should().Contain(r => r.Type == "landmark_telepad");
        refs.Should().Contain(r => r.Type == "landmark_npc" && r.Name == "Marna" && r.World.X == 30 && r.World.Z == 40);
    }

    [Fact]
    public void Skips_landmarks_with_a_malformed_or_missing_loc()
    {
        var refData = new FakeAreaReferenceData()
            .WithLandmarks("AreaSerbule",
                new Landmark { Name = "Good", Loc = "x:1 y:2 z:3", Type = "Portal" },
                new Landmark { Name = "NoLoc", Loc = null, Type = "Portal" },
                new Landmark { Name = "Garbage", Loc = "not a position", Type = "Portal" });

        var refs = new ReferenceDataAreaReferenceProvider(refData).ForArea("AreaSerbule");

        refs.Should().ContainSingle(r => r.Type == "landmark_portal");
        refs.Should().OnlyContain(r => r.Name == "Good");
    }

    [Fact]
    public void Drops_an_unmapped_landmark_type_rather_than_guessing_a_token()
    {
        // An unknown Type must not be silently coerced to a wrong token; it is
        // dropped (and warned, verification-owed). A wrong token would mispair
        // the detection and corrupt the solve.
        var refData = new FakeAreaReferenceData()
            .WithLandmarks("AreaSerbule",
                new Landmark { Name = "Mystery", Loc = "x:1 y:0 z:1", Type = "SomeFutureLandmark" });

        new ReferenceDataAreaReferenceProvider(refData).ForArea("AreaSerbule").Should().BeEmpty();
    }

    [Fact]
    public void Unknown_area_yields_empty()
        => new ReferenceDataAreaReferenceProvider(new FakeAreaReferenceData()).ForArea("AreaNope").Should().BeEmpty();
}
