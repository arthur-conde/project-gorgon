using System.IO;
using System.Text.Json;
using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Gorgon.Shared.Character;
using Xunit;

namespace Gandalf.Tests;

public class GandalfTimerTests
{
    [Fact]
    public void New_timer_is_idle()
    {
        var timer = new GandalfTimer { Duration = TimeSpan.FromHours(1) };

        timer.State.Should().Be(TimerState.Idle);
        timer.Remaining.Should().Be(TimeSpan.FromHours(1));
        timer.Fraction.Should().Be(0.0);
    }

    [Fact]
    public void Started_timer_is_running()
    {
        var timer = new GandalfTimer { Duration = TimeSpan.FromHours(1) };
        timer.Start();

        timer.State.Should().Be(TimerState.Running);
        timer.Remaining.Should().BeGreaterThan(TimeSpan.Zero);
        timer.Fraction.Should().BeInRange(0.0, 0.01);
    }

    [Fact]
    public void Timer_started_in_the_past_is_done()
    {
        var timer = new GandalfTimer
        {
            Duration = TimeSpan.FromMinutes(30),
            StartedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(1),
        };

        timer.State.Should().Be(TimerState.Done);
        timer.Remaining.Should().Be(TimeSpan.Zero);
        timer.Fraction.Should().Be(1.0);
    }

    [Fact]
    public void Fraction_at_midpoint()
    {
        var timer = new GandalfTimer
        {
            Duration = TimeSpan.FromHours(2),
            StartedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(1),
        };

        timer.State.Should().Be(TimerState.Running);
        timer.Fraction.Should().BeApproximately(0.5, 0.01);
    }

    [Fact]
    public void Restart_resets_to_running()
    {
        var timer = new GandalfTimer
        {
            Duration = TimeSpan.FromMinutes(5),
            StartedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10),
            CompletedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5),
        };

        timer.State.Should().Be(TimerState.Done);
        timer.Restart();
        timer.State.Should().Be(TimerState.Running);
        timer.CompletedAt.Should().BeNull();
    }

    [Fact]
    public void GroupKey_includes_region_and_map()
    {
        var timer = new GandalfTimer { Region = "Serbule", Map = "Serbule Sewers" };
        timer.GroupKey.Should().Be("Serbule > Serbule Sewers");
    }

    [Fact]
    public void GroupKey_region_only_when_map_blank()
    {
        var timer = new GandalfTimer { Region = "Serbule", Map = "" };
        timer.GroupKey.Should().Be("Serbule");
    }

    [Fact]
    public void State_roundtrips_through_json_with_running_timer()
    {
        var state = new GandalfState
        {
            Timers =
            [
                new GandalfTimer
                {
                    Name = "Chest",
                    Duration = TimeSpan.FromHours(1),
                    StartedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10),
                    Region = "Serbule",
                    Map = "Serbule",
                },
                new GandalfTimer
                {
                    Name = "Idle one",
                    Duration = TimeSpan.FromMinutes(30),
                    Region = "Eltibule",
                    Map = "",
                },
            ],
        };

        var json = JsonSerializer.Serialize(state, GandalfStateJsonContext.Default.GandalfState);

        // Computed properties must not appear in serialized output
        json.Should().NotContain("\"state\"");
        json.Should().NotContain("\"remaining\"");
        json.Should().NotContain("\"fraction\"");
        json.Should().NotContain("\"groupKey\"");

        var restored = JsonSerializer.Deserialize(json, GandalfStateJsonContext.Default.GandalfState);

        restored.Should().NotBeNull();
        restored!.Timers.Should().HaveCount(2);

        var running = restored.Timers[0];
        running.Name.Should().Be("Chest");
        running.StartedAt.Should().NotBeNull();
        running.State.Should().Be(TimerState.Running);
        running.Remaining.Should().BeGreaterThan(TimeSpan.Zero);

        var idle = restored.Timers[1];
        idle.Name.Should().Be("Idle one");
        idle.StartedAt.Should().BeNull();
        idle.State.Should().Be(TimerState.Idle);
    }

    [Fact]
    public void State_survives_file_roundtrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gandalf_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var active = new FakeActiveCharacterService();
            active.SetActiveCharacter("Arthur", "Kwatoxi");
            var store = new PerCharacterStore<GandalfState>(dir, "gandalf.json",
                GandalfStateJsonContext.Default.GandalfState);

            // Simulate: create service, add timer, start it, dispose (flush to disk)
            using (var view = new PerCharacterView<GandalfState>(active, store))
            {
                var svc = new TimerStateService(view);
                svc.Add(new GandalfTimer
                {
                    Name = "Test",
                    Duration = TimeSpan.FromHours(1),
                    Region = "Serbule",
                    Map = "Serbule",
                });
                svc.Start(svc.Timers[0].Id);
                svc.Timers[0].StartedAt.Should().NotBeNull();
                svc.Dispose(); // triggers Flush
            }

            // Simulate: new app session, load from disk
            using var view2 = new PerCharacterView<GandalfState>(active, store);
            var svc2 = new TimerStateService(view2);

            svc2.Timers.Should().HaveCount(1);
            svc2.Timers[0].Name.Should().Be("Test");
            svc2.Timers[0].StartedAt.Should().NotBeNull("StartedAt must survive restart");
            svc2.Timers[0].State.Should().Be(TimerState.Running);
            svc2.Dispose();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Legacy_json_with_computed_properties_still_deserializes()
    {
        // Old state files contain computed read-only properties; ensure they're harmlessly ignored
        var json = """
        {
          "timers": [
            {
              "id": "abc123",
              "name": "Legacy",
              "duration": "01:00:00",
              "region": "Serbule",
              "map": "Serbule",
              "startedAt": "2026-04-18T10:00:00+00:00",
              "completedAt": null,
              "state": 1,
              "remaining": "00:30:00",
              "fraction": 0.5,
              "groupKey": "Serbule > Serbule"
            }
          ]
        }
        """;

        var restored = JsonSerializer.Deserialize(json, GandalfStateJsonContext.Default.GandalfState);

        restored.Should().NotBeNull();
        restored!.Timers.Should().HaveCount(1);
        var timer = restored.Timers[0];
        timer.Id.Should().Be("abc123");
        timer.Name.Should().Be("Legacy");
        timer.StartedAt.Should().NotBeNull();
        // State is computed, not from JSON — this timer started long ago so it's Done
        timer.State.Should().Be(TimerState.Done);
    }
}
