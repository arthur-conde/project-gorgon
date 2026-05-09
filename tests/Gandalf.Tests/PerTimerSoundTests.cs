using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Xunit;

namespace Gandalf.Tests;

/// <summary>
/// Coverage for the per-timer alarm sound path: definitions carry an optional
/// <see cref="GandalfTimerDef.SoundFilePath"/>, the alarm service picks
/// per-timer over global, and the clipboard format round-trips the field so
/// copy/paste preserves a custom sound.
/// </summary>
public class PerTimerSoundTests
{
    [Fact]
    public void ResolveSoundPath_prefers_per_timer_path_when_set()
    {
        var def = new GandalfTimerDef { SoundFilePath = @"C:\custom\dawn.wav" };
        var settings = new GandalfSettings { SoundFilePath = @"C:\global\default.wav" };
        var ev = new TimerReadyEventArgs
        {
            SourceId = UserTimerSource.Id,
            Key = def.Id,
            DisplayName = "Dawn",
            ReadyAt = DateTimeOffset.UtcNow,
            SourceMetadata = def,
        };

        TimerAlarmService.ResolveSoundPath(ev, settings).Should().Be(@"C:\custom\dawn.wav");
    }

    [Fact]
    public void ResolveSoundPath_falls_back_to_global_when_per_timer_is_null()
    {
        var def = new GandalfTimerDef();  // SoundFilePath null
        var settings = new GandalfSettings { SoundFilePath = @"C:\global\default.wav" };
        var ev = new TimerReadyEventArgs
        {
            SourceId = UserTimerSource.Id,
            Key = def.Id,
            DisplayName = "Plain",
            ReadyAt = DateTimeOffset.UtcNow,
            SourceMetadata = def,
        };

        TimerAlarmService.ResolveSoundPath(ev, settings).Should().Be(@"C:\global\default.wav");
    }

    [Fact]
    public void ResolveSoundPath_returns_null_when_neither_is_set()
    {
        // AudioPlayer.Play handles null by playing the system default — the
        // resolver doesn't substitute a fallback path itself.
        var def = new GandalfTimerDef();
        var settings = new GandalfSettings();
        var ev = new TimerReadyEventArgs
        {
            SourceId = UserTimerSource.Id,
            Key = def.Id,
            DisplayName = "Plain",
            ReadyAt = DateTimeOffset.UtcNow,
            SourceMetadata = def,
        };

        TimerAlarmService.ResolveSoundPath(ev, settings).Should().BeNull();
    }

    [Fact]
    public void ResolveSoundPath_falls_back_to_global_for_non_user_sources()
    {
        // Quest/Loot rows don't carry a GandalfTimerDef in SourceMetadata, so
        // the per-timer override doesn't apply to derived sources. They get
        // the global default — same behavior as today.
        var settings = new GandalfSettings { SoundFilePath = @"C:\global\default.wav" };
        var ev = new TimerReadyEventArgs
        {
            SourceId = "gandalf.quest",
            Key = "some-quest-key",
            DisplayName = "Quest",
            ReadyAt = DateTimeOffset.UtcNow,
            SourceMetadata = new { Foo = "bar" },
        };

        TimerAlarmService.ResolveSoundPath(ev, settings).Should().Be(@"C:\global\default.wav");
    }

    [Fact]
    public void TimerClipboard_roundtrips_SoundFilePath()
    {
        var original = new GandalfTimerDef
        {
            Name = "Sounded",
            Duration = TimeSpan.FromMinutes(15),
            Region = "Serbule",
            Map = "Serbule",
            SoundFilePath = @"C:\custom\chime.wav",
        };

        var json = TimerClipboard.Serialize([original]);
        var entries = TimerClipboard.TryDeserialize(json);
        entries.Should().NotBeNull().And.HaveCount(1);

        var roundTripped = TimerClipboard.ToDef(entries![0]);
        roundTripped.Should().NotBeNull();
        roundTripped!.SoundFilePath.Should().Be(@"C:\custom\chime.wav");
    }

    [Fact]
    public void TimerClipboard_roundtrips_null_SoundFilePath_as_null()
    {
        var original = new GandalfTimerDef
        {
            Name = "Default",
            Duration = TimeSpan.FromMinutes(15),
            SoundFilePath = null,
        };

        var json = TimerClipboard.Serialize([original]);
        var entries = TimerClipboard.TryDeserialize(json);
        entries.Should().NotBeNull().And.HaveCount(1);

        var roundTripped = TimerClipboard.ToDef(entries![0]);
        roundTripped!.SoundFilePath.Should().BeNull();
    }
}
