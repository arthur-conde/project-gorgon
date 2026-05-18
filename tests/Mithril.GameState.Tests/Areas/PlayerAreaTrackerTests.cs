using System.IO;
using System.Text;
using FluentAssertions;
using Mithril.GameState.Areas;
using Mithril.GameState.Areas.Parsing;
using Xunit;

namespace Mithril.GameState.Tests.Areas;

[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class PlayerAreaTrackerTests : IDisposable
{
    private readonly string _tempDir;

    public PlayerAreaTrackerTests()
    {
        _tempDir = Mithril.TestSupport.TestPaths.CreateTempDir("gandalf_area_tracker");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private static PlayerAreaTracker Build() => new(new AreaTransitionParser());

    [Fact]
    public void Initial_state_is_null()
    {
        Build().CurrentArea.Should().BeNull();
    }

    [Fact]
    public void Observe_real_area_sets_CurrentArea()
    {
        var tracker = Build();
        tracker.Observe("LOADING LEVEL AreaSerbule", DateTime.UtcNow);
        tracker.CurrentArea.Should().Be("AreaSerbule");
    }

    [Fact]
    public void Observe_portal_transition_replaces_area()
    {
        var tracker = Build();
        tracker.Observe("LOADING LEVEL AreaSerbule", DateTime.UtcNow);
        tracker.Observe("LOADING LEVEL AreaEltibule", DateTime.UtcNow);
        tracker.CurrentArea.Should().Be("AreaEltibule");
    }

    [Fact]
    public void Observe_ChooseCharacter_clears_area()
    {
        var tracker = Build();
        tracker.Observe("LOADING LEVEL AreaSerbule", DateTime.UtcNow);
        tracker.Observe("LOADING LEVEL ChooseCharacter", DateTime.UtcNow);
        tracker.CurrentArea.Should().BeNull();
    }

    [Fact]
    public void Observe_disconnect_clears_area()
    {
        var tracker = Build();
        tracker.Observe("LOADING LEVEL AreaSerbule", DateTime.UtcNow);
        tracker.Observe("LOADING LEVEL ", DateTime.UtcNow);
        tracker.CurrentArea.Should().BeNull();
    }

    [Fact]
    public void Observe_unrelated_line_is_noop()
    {
        var tracker = Build();
        tracker.Observe("LOADING LEVEL AreaSerbule", DateTime.UtcNow);
        tracker.Observe("LocalPlayer: ProcessAddItem(Apple(1), -1, True)", DateTime.UtcNow);
        tracker.Observe("ProcessChat(General, \"hi\")", DateTime.UtcNow);
        tracker.CurrentArea.Should().Be("AreaSerbule");
    }

    [Fact]
    public void SeedFromLog_picks_up_most_recent_area()
    {
        var path = WriteLog(
            "LocalPlayer: ProcessAddItem(Apple(1), -1, True)",
            "LOADING LEVEL AreaSerbule",
            "LocalPlayer: ProcessAddPlayer(...)",
            "LOADING LEVEL AreaEltibule",
            "LocalPlayer: ProcessAddPlayer(...)",
            "(more game noise)");
        var tracker = Build();
        tracker.SeedFromLog(path);
        tracker.CurrentArea.Should().Be("AreaEltibule");
    }

    [Fact]
    public void SeedFromLog_with_disconnect_clears_area()
    {
        var path = WriteLog(
            "LOADING LEVEL AreaSerbule",
            "(player plays for a while)",
            "LOADING LEVEL ");
        var tracker = Build();
        tracker.SeedFromLog(path);
        tracker.CurrentArea.Should().BeNull();
    }

    [Fact]
    public void SeedFromLog_with_no_marker_is_noop()
    {
        var path = WriteLog(
            "LocalPlayer: ProcessAddItem(Apple(1), -1, True)",
            "LocalPlayer: ProcessAddPlayer(...)",
            "(no area transition lines)");
        var tracker = Build();
        tracker.SeedFromLog(path);
        tracker.CurrentArea.Should().BeNull();
    }

    [Fact]
    public void SeedFromLog_missing_file_is_noop()
    {
        var tracker = Build();
        tracker.SeedFromLog(Path.Combine(_tempDir, "does-not-exist.log"));
        tracker.CurrentArea.Should().BeNull();
    }

    [Fact]
    public void SeedFromLog_then_live_observe_advances_state()
    {
        var path = WriteLog(
            "LOADING LEVEL AreaSerbule");
        var tracker = Build();
        tracker.SeedFromLog(path);
        tracker.CurrentArea.Should().Be("AreaSerbule");

        // Live tail then observes a fresh portal.
        tracker.Observe("LOADING LEVEL AreaTomb1", DateTime.UtcNow);
        tracker.CurrentArea.Should().Be("AreaTomb1");
    }

    private string WriteLog(params string[] lines)
    {
        var path = Path.Combine(_tempDir, $"player_{Guid.NewGuid():N}.log");
        // Real Player.log doesn't carry a UTF-8 BOM; Encoding.UTF8 in .NET
        // emits one by default (﻿ prefix), which would defeat the
        // parser's "^" anchor. Use BOM-less UTF-8 to mirror live shape.
        File.WriteAllText(path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
        return path;
    }
}
