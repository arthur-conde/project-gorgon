using FluentAssertions;
using Gandalf.Domain;
using Xunit;

namespace Gandalf.Tests;

public class ClipboardFormatTests
{
    [Fact]
    public void Serialize_roundtrips_through_deserialize()
    {
        var def = new GandalfTimerDef
        {
            Name = "Mushroom Substrate",
            Duration = TimeSpan.FromHours(4) + TimeSpan.FromMinutes(30),
            Area = "Serbule",
            AreaKey = "AreaSerbule",
        };

        var json = TimerClipboard.Serialize([def]);
        var entries = TimerClipboard.TryDeserialize(json);

        entries.Should().NotBeNull();
        entries.Should().HaveCount(1);
        entries![0].Name.Should().Be("Mushroom Substrate");
        entries[0].Area.Should().Be("Serbule");
        entries[0].AreaKey.Should().Be("AreaSerbule");
        TimeSpan.TryParse(entries[0].Duration, out var dur).Should().BeTrue();
        dur.Should().Be(TimeSpan.FromHours(4) + TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Deserialize_single_object()
    {
        var json = """{"name":"Test","duration":"1:00:00","area":"Serbule","areaKey":"AreaSerbule"}""";
        var entries = TimerClipboard.TryDeserialize(json);

        entries.Should().NotBeNull();
        entries.Should().HaveCount(1);
        entries![0].Name.Should().Be("Test");
    }

    [Fact]
    public void Deserialize_array()
    {
        var json = """[{"name":"A","duration":"0:30:00","area":"Serbule","areaKey":"AreaSerbule"},{"name":"B","duration":"1:00:00","area":"My Hideout","areaKey":null}]""";
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
    public void ToDef_rejects_zero_duration()
    {
        var entry = new TimerClipboardEntry { Name = "X", Duration = "0:00:00", Area = "Serbule" };
        TimerClipboard.ToDef(entry).Should().BeNull();
    }

    [Fact]
    public void ToDef_creates_fresh_def_with_new_id()
    {
        var entry = new TimerClipboardEntry
        {
            Name = "Leather Tanning",
            Duration = "2:30:00",
            Area = "Eltibule",
            AreaKey = "AreaEltibule",
        };

        var def = TimerClipboard.ToDef(entry);

        def.Should().NotBeNull();
        def!.Name.Should().Be("Leather Tanning");
        def.Duration.Should().Be(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(30));
        def.Area.Should().Be("Eltibule");
        def.AreaKey.Should().Be("AreaEltibule");
        def.Id.Should().NotBeNullOrEmpty();
    }
}
