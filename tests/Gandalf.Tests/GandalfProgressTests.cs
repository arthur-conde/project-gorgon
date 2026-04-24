using System.Text.Json;
using FluentAssertions;
using Gandalf.Domain;
using Xunit;

namespace Gandalf.Tests;

public class GandalfProgressTests
{
    [Fact]
    public void Progress_roundtrip_preserves_entries()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new GandalfProgress
        {
            ByTimerId =
            {
                ["abc"] = new TimerProgress { StartedAt = now - TimeSpan.FromMinutes(10) },
                ["xyz"] = new TimerProgress
                {
                    StartedAt = now - TimeSpan.FromHours(2),
                    CompletedAt = now - TimeSpan.FromHours(1),
                },
                ["idle"] = new TimerProgress(),
            },
        };

        var json = JsonSerializer.Serialize(state, GandalfProgressJsonContext.Default.GandalfProgress);
        var restored = JsonSerializer.Deserialize(json, GandalfProgressJsonContext.Default.GandalfProgress);

        restored.Should().NotBeNull();
        restored!.SchemaVersion.Should().Be(GandalfProgress.Version);
        restored.ByTimerId.Should().HaveCount(3);
        restored.ByTimerId["abc"].StartedAt.Should().BeCloseTo(state.ByTimerId["abc"].StartedAt!.Value, TimeSpan.FromMilliseconds(1));
        restored.ByTimerId["abc"].CompletedAt.Should().BeNull();
        restored.ByTimerId["xyz"].CompletedAt.Should().NotBeNull();
        restored.ByTimerId["idle"].StartedAt.Should().BeNull();
    }

    [Fact]
    public void Migrate_drops_v1_shape_payload()
    {
        // A v1 payload that sneaks past the fanout would carry a schema version < 2 —
        // Migrate must strip it, not try to interpret it as the new shape.
        var legacy = new GandalfProgress { SchemaVersion = 1 };
        legacy.ByTimerId["stale"] = new TimerProgress { StartedAt = DateTimeOffset.UtcNow };

        var migrated = GandalfProgress.Migrate(legacy);

        migrated.SchemaVersion.Should().Be(GandalfProgress.Version);
        migrated.ByTimerId.Should().BeEmpty();
    }

    [Fact]
    public void Migrate_is_identity_for_current_version()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new GandalfProgress
        {
            SchemaVersion = GandalfProgress.Version,
            ByTimerId = { ["abc"] = new TimerProgress { StartedAt = now } },
        };

        var migrated = GandalfProgress.Migrate(state);
        migrated.Should().BeSameAs(state);
        migrated.ByTimerId.Should().ContainKey("abc");
    }

    [Fact]
    public void New_progress_uses_ordinal_dictionary_comparer()
    {
        // Timer ids are GUIDs (hex) — comparer choice affects lookup correctness post-migration.
        var state = new GandalfProgress();
        state.ByTimerId["abc"] = new TimerProgress();
        state.ByTimerId.ContainsKey("ABC").Should().BeFalse("ids are case-sensitive");
    }
}
