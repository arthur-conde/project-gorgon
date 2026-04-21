using FluentAssertions;
using Gandalf.Domain;
using Xunit;

namespace Gandalf.Tests;

public class ClipboardFormatTests
{
    [Fact]
    public void Serialize_roundtrips_through_deserialize()
    {
        var timer = new GandalfTimer
        {
            Name = "Mushroom Substrate",
            Duration = TimeSpan.FromHours(4) + TimeSpan.FromMinutes(30),
            Region = "Serbule",
            Map = "Serbule Sewers",
        };

        var json = TimerClipboard.Serialize([timer]);
        var entries = TimerClipboard.TryDeserialize(json);

        entries.Should().NotBeNull();
        entries.Should().HaveCount(1);
        entries![0].Name.Should().Be("Mushroom Substrate");
        entries[0].Region.Should().Be("Serbule");
        entries[0].Map.Should().Be("Serbule Sewers");
        TimeSpan.TryParse(entries[0].Duration, out var dur).Should().BeTrue();
        dur.Should().Be(TimeSpan.FromHours(4) + TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Deserialize_single_object()
    {
        var json = """{"name":"Test","duration":"1:00:00","region":"R","map":"M"}""";
        var entries = TimerClipboard.TryDeserialize(json);

        entries.Should().NotBeNull();
        entries.Should().HaveCount(1);
        entries![0].Name.Should().Be("Test");
    }

    [Fact]
    public void Deserialize_array()
    {
        var json = """[{"name":"A","duration":"0:30:00","region":"R1","map":"M1"},{"name":"B","duration":"1:00:00","region":"R2","map":"M2"}]""";
        var entries = TimerClipboard.TryDeserialize(json);

        entries.Should().NotBeNull();
        entries.Should().HaveCount(2);
    }

    [Fact]
    public void Deserialize_invalid_returns_null()
    {
        TimerClipboard.TryDeserialize("not json").Should().BeNull();
        TimerClipboard.TryDeserialize("").Should().BeNull();
    }

    [Fact]
    public void ToTimer_rejects_zero_duration()
    {
        var entry = new TimerClipboardEntry { Name = "X", Duration = "0:00:00", Region = "R", Map = "M" };
        TimerClipboard.ToTimer(entry).Should().BeNull();
    }

    [Fact]
    public void ToTimer_creates_idle_timer()
    {
        var entry = new TimerClipboardEntry
        {
            Name = "Leather Tanning",
            Duration = "2:30:00",
            Region = "Eltibule",
            Map = "Eltibule Keep",
        };

        var timer = TimerClipboard.ToTimer(entry);

        timer.Should().NotBeNull();
        timer!.Name.Should().Be("Leather Tanning");
        timer.Duration.Should().Be(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(30));
        timer.Region.Should().Be("Eltibule");
        timer.Map.Should().Be("Eltibule Keep");
        timer.State.Should().Be(TimerState.Idle);
        timer.StartedAt.Should().BeNull();
    }
}
