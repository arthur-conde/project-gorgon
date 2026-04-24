using System.Text.Json;
using FluentAssertions;
using Gandalf.Domain;
using Xunit;

namespace Gandalf.Tests;

public class GandalfDefinitionsTests
{
    [Fact]
    public void Definitions_roundtrip_preserves_fields()
    {
        var defs = new GandalfDefinitions
        {
            Timers =
            [
                new GandalfTimerDef
                {
                    Id = "abc123",
                    Name = "Chest",
                    Duration = TimeSpan.FromHours(1),
                    Region = "Serbule",
                    Map = "Serbule",
                },
                new GandalfTimerDef
                {
                    Id = "def456",
                    Name = "Crypt",
                    Duration = TimeSpan.FromMinutes(30),
                    Region = "Eltibule",
                    Map = "",
                },
            ],
        };

        var json = JsonSerializer.Serialize(defs, GandalfDefinitionsJsonContext.Default.GandalfDefinitions);
        // Computed GroupKey must not leak into persisted JSON.
        json.Should().NotContain("\"groupKey\"");

        var restored = JsonSerializer.Deserialize(json, GandalfDefinitionsJsonContext.Default.GandalfDefinitions);
        restored.Should().NotBeNull();
        restored!.SchemaVersion.Should().Be(GandalfDefinitions.Version);
        restored.Timers.Should().HaveCount(2);
        restored.Timers[0].Id.Should().Be("abc123");
        restored.Timers[0].Name.Should().Be("Chest");
        restored.Timers[0].Duration.Should().Be(TimeSpan.FromHours(1));
        restored.Timers[0].GroupKey.Should().Be("Serbule > Serbule");
        restored.Timers[1].GroupKey.Should().Be("Eltibule");
    }

    [Fact]
    public void New_definitions_has_no_timers_and_current_version()
    {
        var defs = new GandalfDefinitions();
        defs.Timers.Should().BeEmpty();
        defs.SchemaVersion.Should().Be(GandalfDefinitions.Version);
    }
}
