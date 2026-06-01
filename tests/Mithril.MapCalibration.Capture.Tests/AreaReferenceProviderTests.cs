using System.Linq;
using FluentAssertions;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.MapCalibration.Detection;
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

        // mithril#974: Type carries the raw PG vocabulary (the same the detector
        // keys IconTemplate.LandmarkType by); the landmark_* sprite strings are
        // template NAMES, never types.
        refs.Should().Contain(r => r.Type == "Portal" && r.World.X == 10 && r.World.Z == -20);
        refs.Should().Contain(r => r.Type == "MeditationPillar");
        refs.Should().Contain(r => r.Type == "TeleportationPlatform");
        refs.Should().Contain(r => r.Type == "Npc" && r.Name == "Marna" && r.World.X == 30 && r.World.Z == 40);
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

        refs.Should().ContainSingle(r => r.Type == "Portal");
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

    [Fact]
    public void Emitted_types_are_a_subset_of_the_canonical_vocabulary()
    {
        // mithril#974 seam guard: every reference Type the provider emits must be
        // in CanonicalLandmarkTypes.All — the same vocabulary the detector keys
        // IconTemplate.LandmarkType by — so the type-constrained solver can pair
        // them. A stray landmark_* token here would re-open the 0-inliers bug.
        var refData = new FakeAreaReferenceData()
            .WithLandmarks("AreaSerbule",
                new Landmark { Name = "P", Loc = "x:1 y:0 z:1", Type = "Portal" },
                new Landmark { Name = "M", Loc = "x:2 y:0 z:2", Type = "MeditationPillar" },
                new Landmark { Name = "T", Loc = "x:3 y:0 z:3", Type = "TeleportationPlatform" })
            .WithNpc("AreaSerbule", "Marna", pos: "x:4 y:0 z:4");

        var refs = new ReferenceDataAreaReferenceProvider(refData).ForArea("AreaSerbule");

        refs.Select(r => r.Type).Distinct().Should().OnlyContain(t => CanonicalLandmarkTypes.All.Contains(t));
        refs.Should().NotBeEmpty();
    }
}
