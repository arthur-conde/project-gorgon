using System.IO;
using System.Text;
using FluentAssertions;
using Mithril.GameState.Areas;
using Mithril.GameState.Areas.Parsing;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Game;
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

    // ---- #514: tracker owns its one-shot seed; consumers only read --------

    private sealed class CapturingSink : IDiagnosticsSink
    {
        public List<(DiagnosticLevel Level, string Category, string Message)> Entries { get; } = new();
        public void Write(DiagnosticLevel level, string category, string message) =>
            Entries.Add((level, category, message));
        public IReadOnlyList<DiagnosticEntry> Snapshot() => Array.Empty<DiagnosticEntry>();
        public event EventHandler<DiagnosticEntry>? EntryAdded { add { } remove { } }
    }

    /// <summary>Writes <c>Player.log</c> into a fresh dir and returns a
    /// <see cref="GameConfig"/> whose <c>PlayerLogPath</c> points at it.</summary>
    private (GameConfig cfg, string logPath) WriteGameRoot(params string[] lines)
    {
        var root = Path.Combine(_tempDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "Player.log");
        File.WriteAllText(path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
        return (new GameConfig { GameRoot = root }, path);
    }

    [Fact]
    public void Self_seeds_lazily_on_first_CurrentArea_read_without_explicit_SeedFromLog()
    {
        var (cfg, _) = WriteGameRoot(
            "LOADING LEVEL AreaSerbule",
            "LocalPlayer: ProcessAddPlayer(...)");
        var tracker = new PlayerAreaTracker(new AreaTransitionParser(), diag: null, config: cfg);

        // No consumer calls SeedFromLog — the read triggers the owned seed.
        tracker.CurrentArea.Should().Be("AreaSerbule");
    }

    [Fact]
    public void Self_seeds_lazily_on_first_Observe()
    {
        var (cfg, _) = WriteGameRoot("LOADING LEVEL AreaEltibule");
        var tracker = new PlayerAreaTracker(new AreaTransitionParser(), diag: null, config: cfg);

        tracker.Observe("LocalPlayer: ProcessAddItem(Apple(1), -1, True)", DateTime.UtcNow);

        tracker.CurrentArea.Should().Be("AreaEltibule");
    }

    [Fact]
    public void Self_seed_runs_at_most_once()
    {
        var (cfg, logPath) = WriteGameRoot("LOADING LEVEL AreaSerbule");
        var tracker = new PlayerAreaTracker(new AreaTransitionParser(), diag: null, config: cfg);

        tracker.CurrentArea.Should().Be("AreaSerbule");          // seeds once

        // Rewrite the log; a second read must NOT re-scan (idempotent one-shot).
        File.WriteAllText(logPath, "LOADING LEVEL AreaEltibule\n", new UTF8Encoding(false));
        tracker.CurrentArea.Should().Be("AreaSerbule");
    }

    [Fact]
    public void Unknown_area_after_seed_is_surfaced_once_not_a_silent_null()
    {
        var sink = new CapturingSink();
        // GameRoot set but no Player.log written ⇒ seed finds nothing.
        var root = Path.Combine(_tempDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tracker = new PlayerAreaTracker(
            new AreaTransitionParser(), sink, new GameConfig { GameRoot = root });

        tracker.CurrentArea.Should().BeNull();
        _ = tracker.CurrentArea;     // second read must not re-warn

        sink.Entries.Should().ContainSingle()
            .Which.Should().Match<(DiagnosticLevel Level, string Category, string Message)>(
                e => e.Level == DiagnosticLevel.Info
                  && e.Category == "GameState.Area"
                  && e.Message.Contains("Area unknown"));
    }

    [Fact]
    public void Explicit_SeedFromLog_suppresses_the_later_lazy_rescan()
    {
        var (cfg, _) = WriteGameRoot("LOADING LEVEL AreaSerbule");
        var otherPath = WriteLog("LOADING LEVEL AreaTomb1");
        var tracker = new PlayerAreaTracker(new AreaTransitionParser(), diag: null, config: cfg);

        tracker.SeedFromLog(otherPath);                 // explicit seed marks attempted
        tracker.CurrentArea.Should().Be("AreaTomb1");   // lazy seed must NOT override with cfg's area
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
