# Samwise Alarm Channels Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add per-stage `Loop`, per-stage `ChannelId`, and `AlarmChannel { Mix | Replace | Suppress }` collision policy to Samwise alarms; bundle the shell-side `AudioPlayer.ConcurrentPlayback` flip and remove the now-redundant "Allow concurrent alarm sounds" checkbox from both module settings views.

**Architecture:** Channels are user-named groupings stored on `AlarmSettings`; each `StageAlarmRule` carries a `ChannelId` string and a `Loop` flag. `AlarmService` rekeys playback from per-alarm to per-channel — `Dictionary<channelId, List<ChannelOwner>>` — so Mix can hold concurrent handles while Replace/Suppress see at most one. `AudioPlayer` gains a `loop: bool` parameter (wrapping the reader in a small internal `LoopStream`) and `ConcurrentPlayback` defaults to `true`. Tests inject an `IAudioPlaybackSink` so the service is decoupled from the static `AudioPlayer`. Settings migration runs through `IPostLoadInit`-style deserialize-time normalization (no `IVersionedState` today; tracked separately in #208).

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm, NAudio, xunit + FluentAssertions, `System.Text.Json` source-generated contexts, `Microsoft.Extensions.DependencyInjection`.

**Spec reference:** [docs/agent-plans/2026-05-11-samwise-alarm-channels.md](../../agent-plans/2026-05-11-samwise-alarm-channels.md)

---

## File Structure

**New files:**
- `src/Mithril.Shared/Audio/IAudioPlaybackSink.cs` — DI seam over the static `AudioPlayer`
- `src/Mithril.Shared/Audio/StaticAudioPlayerSink.cs` — default impl forwarding to `AudioPlayer.Play/Stop`
- `src/Samwise.Module/Alarms/AlarmChannel.cs` — `AlarmChannel` + `AlarmCollisionBehavior` enum
- `tests/Samwise.Tests/Alarms/FakeAudioPlaybackSink.cs` — recording fake for service tests
- `tests/Samwise.Tests/Alarms/AlarmChannelTests.cs` — POCO behavior tests
- `tests/Samwise.Tests/Alarms/AlarmSettingsTests.cs` — defaults + migration tests
- `tests/Samwise.Tests/Alarms/AlarmServiceTests.cs` — channel-scoped service tests

**Modified files:**
- `src/Mithril.Shared/Audio/AudioPlayer.cs` — add `loop: bool` param + internal `LoopStream`, flip `ConcurrentPlayback` default
- `src/Mithril.Shared/DependencyInjection/ServiceCollectionExtensions.cs` — register `IAudioPlaybackSink`
- `src/Samwise.Module/Alarms/AlarmSettings.cs` — `Loop`, `ChannelId` on `StageAlarmRule`; `Channels` + `MembershipSummary` on `AlarmSettings`; `IPostLoadInit`
- `src/Samwise.Module/Alarms/AlarmService.cs` — channel-keyed playback list, Mix/Replace/Suppress dispatch
- `src/Samwise.Module/SamwiseModule.cs` — inject `IAudioPlaybackSink` into `AlarmService`; drop `Audio = sp.GetRequiredService<AudioSettings>()`
- `src/Samwise.Module/Views/SamwiseSettingsView.xaml` — channels card section + per-stage Loop/Channel rows; remove concurrent checkbox
- `src/Samwise.Module/Views/SamwiseSettingsView.xaml.cs` — channel CRUD handlers; drop `Audio` property
- `src/Gandalf.Module/Views/GandalfSettingsView.xaml` — remove concurrent checkbox
- `src/Gandalf.Module/Views/GandalfSettingsView.xaml.cs` — drop `Audio` property
- `src/Gandalf.Module/GandalfModule.cs` — drop `Audio = sp.GetRequiredService<AudioSettings>()`
- `src/Mithril.Shell/ShellSettings.cs` — delete `ConcurrentAlarms`
- `src/Mithril.Shell/Program.cs` — delete `AudioSettings` construction + persistence wiring

**Deleted files:**
- `src/Mithril.Shared/Settings/AudioSettings.cs` — type no longer needed

---

## Task 1: IAudioPlaybackSink test seam

**Files:**
- Create: `src/Mithril.Shared/Audio/IAudioPlaybackSink.cs`
- Create: `src/Mithril.Shared/Audio/StaticAudioPlayerSink.cs`
- Modify: `src/Mithril.Shared/DependencyInjection/ServiceCollectionExtensions.cs`
- Create: `tests/Samwise.Tests/Alarms/FakeAudioPlaybackSink.cs`

### - [ ] Step 1.1: Create the interface

Write `src/Mithril.Shared/Audio/IAudioPlaybackSink.cs`:

```csharp
namespace Mithril.Shared.Audio;

/// <summary>
/// DI seam over the static <see cref="AudioPlayer"/>. Production registers
/// <see cref="StaticAudioPlayerSink"/> which forwards; tests inject a
/// recording fake so AlarmService becomes unit-testable without WPF or NAudio.
/// </summary>
public interface IAudioPlaybackSink
{
    IPlaybackHandle Play(string? path, float volume = 1.0f, string? callerId = null, bool loop = false);
    void Stop();
    void Stop(string callerId);
}
```

### - [ ] Step 1.2: Create the static-forwarding default impl

Write `src/Mithril.Shared/Audio/StaticAudioPlayerSink.cs`:

```csharp
namespace Mithril.Shared.Audio;

internal sealed class StaticAudioPlayerSink : IAudioPlaybackSink
{
    public IPlaybackHandle Play(string? path, float volume = 1.0f, string? callerId = null, bool loop = false)
        => AudioPlayer.Play(path, volume, callerId, loop);

    public void Stop() => AudioPlayer.Stop();
    public void Stop(string callerId) => AudioPlayer.Stop(callerId);
}
```

Note: `AudioPlayer.Play` doesn't have the `loop` parameter yet — it gets added in Task 2. This file won't compile until Task 2 lands. Fine for plan ordering; Task 2 closes the gap before any test attempts to run.

### - [ ] Step 1.3: Register the sink in DI

Open `src/Mithril.Shared/DependencyInjection/ServiceCollectionExtensions.cs`. Find the existing audio registrations (search for `Audio` or `AudioSettings`). Add:

```csharp
services.AddSingleton<IAudioPlaybackSink, StaticAudioPlayerSink>();
```

If there's no obvious audio-section pattern, put it adjacent to the existing `AudioSettings` registration line (which gets deleted in Task 3 — by the time that deletion lands, the sink registration is the clear successor).

### - [ ] Step 1.4: Create the test fake

Write `tests/Samwise.Tests/Alarms/FakeAudioPlaybackSink.cs`:

```csharp
using Mithril.Shared.Audio;

namespace Samwise.Tests.Alarms;

internal sealed class FakeAudioPlaybackSink : IAudioPlaybackSink
{
    public sealed record PlayCall(string? Path, float Volume, string? CallerId, bool Loop, FakePlaybackHandle Handle);

    public List<PlayCall> Plays { get; } = new();
    public int GlobalStopCount { get; private set; }
    public List<string> CallerStops { get; } = new();

    public IPlaybackHandle Play(string? path, float volume = 1.0f, string? callerId = null, bool loop = false)
    {
        var handle = new FakePlaybackHandle();
        Plays.Add(new PlayCall(path, volume, callerId, loop, handle));
        return handle;
    }

    public void Stop() => GlobalStopCount++;
    public void Stop(string callerId) => CallerStops.Add(callerId);
}

internal sealed class FakePlaybackHandle : IPlaybackHandle
{
    public bool Disposed { get; private set; }
    public bool IsPlaying { get; set; } = true;
    public void Stop() { IsPlaying = false; Disposed = true; }
    public void Dispose() => Stop();
}
```

### - [ ] Step 1.5: Confirm the build succeeds with the new files

Run: `dotnet build Mithril.slnx`
Expected: build succeeds (or fails only on `Task 2`'s missing `loop` overload — if so, comment out `StaticAudioPlayerSink` body temporarily, then restore it in Task 2). Easiest: do Task 2 before this builds end-to-end.

### - [ ] Step 1.6: Commit

```bash
git add src/Mithril.Shared/Audio/IAudioPlaybackSink.cs \
        src/Mithril.Shared/Audio/StaticAudioPlayerSink.cs \
        src/Mithril.Shared/DependencyInjection/ServiceCollectionExtensions.cs \
        tests/Samwise.Tests/Alarms/FakeAudioPlaybackSink.cs
git commit -m "feat(shared): add IAudioPlaybackSink DI seam over static AudioPlayer"
```

---

## Task 2: AudioPlayer — loop param + LoopStream + flip ConcurrentPlayback default

**Files:**
- Modify: `src/Mithril.Shared/Audio/AudioPlayer.cs`

### - [ ] Step 2.1: Flip the `ConcurrentPlayback` default

Open `src/Mithril.Shared/Audio/AudioPlayer.cs`. Find the `ConcurrentPlayback` property (currently auto-prop defaulting to `false`):

```csharp
public static bool ConcurrentPlayback { get; set; }
```

Replace with:

```csharp
public static bool ConcurrentPlayback { get; set; } = true;
```

### - [ ] Step 2.2: Add the LoopStream internal helper

At the bottom of the `AudioPlayer` class (after the existing `PlaybackHandle` nested class), add a new nested class:

```csharp
/// <summary>
/// Wraps a <see cref="WaveStream"/> so it loops seamlessly at EOF.
/// Used by <see cref="Play"/> when <c>loop: true</c> is requested.
/// </summary>
private sealed class LoopStream : WaveStream
{
    private readonly WaveStream _inner;
    public LoopStream(WaveStream inner) => _inner = inner;

    public override WaveFormat WaveFormat => _inner.WaveFormat;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = _inner.Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0)
            {
                if (_inner.Position == 0) break; // can't seek; degenerate stream
                _inner.Position = 0;
                continue;
            }
            totalRead += read;
        }
        return totalRead;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
```

### - [ ] Step 2.3: Add `loop` param to `Play`

Find the `Play` method signature:

```csharp
public static IPlaybackHandle Play(string? path, float volume = 1.0f, string? callerId = null)
```

Replace with:

```csharp
public static IPlaybackHandle Play(string? path, float volume = 1.0f, string? callerId = null, bool loop = false)
```

In the method body, locate this block:

```csharp
var reader = OpenReader(path);
var sampleProvider = reader.ToSampleProvider();
```

Wrap the reader when `loop` is true, before the `ToSampleProvider()` call:

```csharp
WaveStream reader = OpenReader(path);
if (loop) reader = new LoopStream(reader);
var sampleProvider = reader.ToSampleProvider();
```

Note: `OpenReader` already returns `WaveStream`; the local variable was previously `var` inferring the concrete type. Change to explicit `WaveStream` so the conditional reassignment compiles.

### - [ ] Step 2.4: Build and confirm

Run: `dotnet build src/Mithril.Shared/Mithril.Shared.csproj`
Expected: build succeeds. `StaticAudioPlayerSink` (created in Task 1) now compiles cleanly with the new `loop` parameter.

Run: `dotnet build Mithril.slnx`
Expected: full solution builds (the only remaining warnings would be unrelated).

### - [ ] Step 2.5: Commit

```bash
git add src/Mithril.Shared/Audio/AudioPlayer.cs
git commit -m "feat(shared): AudioPlayer loop param + flip ConcurrentPlayback default to true"
```

---

## Task 3: Remove ConcurrentAlarms wiring across shell + both module settings

**Files:**
- Delete: `src/Mithril.Shared/Settings/AudioSettings.cs`
- Modify: `src/Mithril.Shell/Program.cs`
- Modify: `src/Mithril.Shell/ShellSettings.cs`
- Modify: `src/Samwise.Module/Views/SamwiseSettingsView.xaml`
- Modify: `src/Samwise.Module/Views/SamwiseSettingsView.xaml.cs`
- Modify: `src/Samwise.Module/SamwiseModule.cs`
- Modify: `src/Gandalf.Module/Views/GandalfSettingsView.xaml`
- Modify: `src/Gandalf.Module/Views/GandalfSettingsView.xaml.cs`
- Modify: `src/Gandalf.Module/GandalfModule.cs`

### - [ ] Step 3.1: Remove `Program.cs` AudioSettings wiring

Open `src/Mithril.Shell/Program.cs`. At line 132, delete:

```csharp
var audioSettings = new AudioSettings { ConcurrentAlarms = shellSettings.ConcurrentAlarms };
```

At the `.AddSingleton(audioSettings)` call (around line 146), delete that line.

At lines 253–262 (the wiring block that runs after host build), delete the entire block:

```csharp
AudioPlayer.ConcurrentPlayback = audioSettings.ConcurrentAlarms;
audioSettings.PropertyChanged += (_, ev) =>
{
    if (ev.PropertyName == nameof(AudioSettings.ConcurrentAlarms))
    {
        AudioPlayer.ConcurrentPlayback = audioSettings.ConcurrentAlarms;
        shellSettings.ConcurrentAlarms = audioSettings.ConcurrentAlarms;
    }
};
```

Remove any now-unused `using Mithril.Shared.Settings;` or similar if it was only there for `AudioSettings`.

### - [ ] Step 3.2: Remove `ShellSettings.ConcurrentAlarms`

Open `src/Mithril.Shell/ShellSettings.cs`. Delete lines 23-24:

```csharp
private bool _concurrentAlarms;
public bool ConcurrentAlarms { get => _concurrentAlarms; set => Set(ref _concurrentAlarms, value); }
```

### - [ ] Step 3.3: Delete `AudioSettings.cs`

```bash
git rm src/Mithril.Shared/Settings/AudioSettings.cs
```

### - [ ] Step 3.4: Remove the Samwise XAML checkbox + binding

Open `src/Samwise.Module/Views/SamwiseSettingsView.xaml`. Delete lines 35-37:

```xml
<CheckBox Content="Allow concurrent alarm sounds"
          IsChecked="{Binding Audio.ConcurrentAlarms, RelativeSource={RelativeSource AncestorType=UserControl}}"
          Foreground="#FFE8E8E8" Margin="0,0,0,4"/>
```

### - [ ] Step 3.5: Remove the Samwise code-behind `Audio` property

Open `src/Samwise.Module/Views/SamwiseSettingsView.xaml.cs`. Delete line 15:

```csharp
public AudioSettings? Audio { get; set; }
```

Remove the `using Mithril.Shared.Settings;` import if no other type from that namespace is used (the existing `IPostLoadInit` usage in later tasks does, so leave it).

### - [ ] Step 3.6: Remove the Samwise DI hookup of `Audio`

Open `src/Samwise.Module/SamwiseModule.cs`. At lines 86-90, the existing block:

```csharp
services.AddSingleton<SamwiseSettingsView>(sp => new SamwiseSettingsView
{
    DataContext = sp.GetRequiredService<SamwiseSettings>(),
    Audio = sp.GetRequiredService<AudioSettings>(),
});
```

Replace with:

```csharp
services.AddSingleton<SamwiseSettingsView>(sp => new SamwiseSettingsView
{
    DataContext = sp.GetRequiredService<SamwiseSettings>(),
});
```

### - [ ] Step 3.7: Remove the Gandalf XAML checkbox + binding

Open `src/Gandalf.Module/Views/GandalfSettingsView.xaml`. Find lines 18-20 (the checkbox bound to `Audio.ConcurrentAlarms`) and delete the entire `<CheckBox ... />` element.

### - [ ] Step 3.8: Remove the Gandalf code-behind `Audio` property

Open `src/Gandalf.Module/Views/GandalfSettingsView.xaml.cs`. Find and delete the line:

```csharp
public AudioSettings? Audio { get; set; }
```

Adjust imports as needed.

### - [ ] Step 3.9: Remove the Gandalf DI hookup of `Audio`

Open `src/Gandalf.Module/GandalfModule.cs`. Find the `services.AddSingleton<GandalfSettingsView>(...)` block (around line 192). Delete the `Audio = sp.GetRequiredService<AudioSettings>(),` line from the object initializer.

### - [ ] Step 3.10: Build and confirm

Run: `dotnet build Mithril.slnx`
Expected: build succeeds with no references to `AudioSettings` remaining. If the build complains about a stray `using Mithril.Shared.Settings;` import where `AudioSettings` was the only symbol used, remove that import.

### - [ ] Step 3.11: Commit

```bash
git add -A
git commit -m "refactor: drop user-facing 'Allow concurrent alarm sounds' toggle

ConcurrentPlayback is now default-on at the AudioPlayer level; channels
(coming next) own intra-module collision policy. The shell setting, the
AudioSettings type, and both settings-view checkboxes are gone."
```

---

## Task 4: AlarmChannel + AlarmCollisionBehavior

**Files:**
- Create: `src/Samwise.Module/Alarms/AlarmChannel.cs`
- Create: `tests/Samwise.Tests/Alarms/AlarmChannelTests.cs`

### - [ ] Step 4.1: Write the failing test

Create `tests/Samwise.Tests/Alarms/AlarmChannelTests.cs`:

```csharp
using FluentAssertions;
using Samwise.Alarms;
using Xunit;

namespace Samwise.Tests.Alarms;

public class AlarmChannelTests
{
    [Fact]
    public void NewChannel_DefaultsToMix()
    {
        var c = new AlarmChannel();
        c.Collision.Should().Be(AlarmCollisionBehavior.Mix);
    }

    [Fact]
    public void NewChannel_HasNonEmptyGuidId()
    {
        var c1 = new AlarmChannel();
        var c2 = new AlarmChannel();
        c1.Id.Should().NotBeNullOrEmpty();
        c1.Id.Should().NotBe(c2.Id);
    }

    [Fact]
    public void SettingName_RaisesPropertyChanged()
    {
        var c = new AlarmChannel { Name = "Default" };
        var raised = new List<string>();
        c.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        c.Name = "Renamed";

        raised.Should().Contain(nameof(AlarmChannel.Name));
    }

    [Fact]
    public void SettingCollision_RaisesPropertyChanged()
    {
        var c = new AlarmChannel();
        var raised = new List<string>();
        c.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        c.Collision = AlarmCollisionBehavior.Replace;

        raised.Should().Contain(nameof(AlarmChannel.Collision));
    }
}
```

### - [ ] Step 4.2: Run the test to verify it fails

Run: `dotnet test tests/Samwise.Tests --filter "FullyQualifiedName~AlarmChannelTests" --no-restore`
Expected: fails to compile (`AlarmChannel`, `AlarmCollisionBehavior` not found).

### - [ ] Step 4.3: Create `AlarmChannel.cs`

Write `src/Samwise.Module/Alarms/AlarmChannel.cs`:

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Samwise.Alarms;

public enum AlarmCollisionBehavior
{
    /// <summary>Concurrent alarms layer freely on this channel.</summary>
    Mix,
    /// <summary>A new alarm stops any currently-playing one on this channel before playing.</summary>
    Replace,
    /// <summary>A new alarm is silenced if any other alarm is currently playing on this channel.</summary>
    Suppress,
}

/// <summary>
/// A user-named group of stage alarms with a shared collision policy.
/// Stages route to channels via <see cref="StageAlarmRule.ChannelId"/>.
/// </summary>
public sealed class AlarmChannel : INotifyPropertyChanged
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    private string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    private AlarmCollisionBehavior _collision = AlarmCollisionBehavior.Mix;
    public AlarmCollisionBehavior Collision { get => _collision; set => Set(ref _collision, value); }

    /// <summary>
    /// Human-readable list of the stages currently routed to this channel.
    /// Recomputed and assigned externally by <see cref="AlarmSettings"/> whenever
    /// rule channel assignments change; AlarmChannel just exposes/notifies.
    /// </summary>
    private string _membershipSummary = "";
    public string MembershipSummary { get => _membershipSummary; set => Set(ref _membershipSummary, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T f, T v, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(f, v)) return;
        f = v;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
```

### - [ ] Step 4.4: Run the tests to verify pass

Run: `dotnet test tests/Samwise.Tests --filter "FullyQualifiedName~AlarmChannelTests"`
Expected: 4 passed.

### - [ ] Step 4.5: Commit

```bash
git add src/Samwise.Module/Alarms/AlarmChannel.cs tests/Samwise.Tests/Alarms/AlarmChannelTests.cs
git commit -m "feat(samwise): AlarmChannel + AlarmCollisionBehavior enum"
```

---

## Task 5: StageAlarmRule new fields (Loop, ChannelId)

**Files:**
- Modify: `src/Samwise.Module/Alarms/AlarmSettings.cs`
- Create: `tests/Samwise.Tests/Alarms/AlarmSettingsTests.cs`

### - [ ] Step 5.1: Write failing tests

Create `tests/Samwise.Tests/Alarms/AlarmSettingsTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using Samwise.Alarms;
using Samwise.State;
using Xunit;

namespace Samwise.Tests.Alarms;

public class AlarmSettingsTests
{
    [Fact]
    public void NewRule_LoopDefaultsToFalse()
    {
        new StageAlarmRule().Loop.Should().BeFalse();
    }

    [Fact]
    public void NewRule_ChannelIdDefaultsToDefault()
    {
        new StageAlarmRule().ChannelId.Should().Be("default");
    }

    [Fact]
    public void StageAlarmRule_JsonRoundTrip_PreservesLoopAndChannel()
    {
        var settings = new SamwiseSettings();
        settings.Alarms.Rules[PlotStage.Ripe].Loop = true;
        settings.Alarms.Rules[PlotStage.Ripe].ChannelId = "custom-channel-id";

        var json = JsonSerializer.Serialize(settings, SamwiseSettingsJsonContext.Default.SamwiseSettings);
        var deserialized = JsonSerializer.Deserialize(json, SamwiseSettingsJsonContext.Default.SamwiseSettings)!;

        var ripe = deserialized.Alarms.Rules[PlotStage.Ripe];
        ripe.Loop.Should().BeTrue();
        ripe.ChannelId.Should().Be("custom-channel-id");
    }
}
```

### - [ ] Step 5.2: Run tests to verify they fail

Run: `dotnet test tests/Samwise.Tests --filter "FullyQualifiedName~AlarmSettingsTests"`
Expected: fails to compile — `Loop` and `ChannelId` not found on `StageAlarmRule`.

### - [ ] Step 5.3: Add `Loop` and `ChannelId` to `StageAlarmRule`

Open `src/Samwise.Module/Alarms/AlarmSettings.cs`. Find the `StageAlarmRule` class. Below the existing `_stopOnInteraction` field/property, add:

```csharp
private bool _loop;
public bool Loop { get => _loop; set => Set(ref _loop, value); }

private string _channelId = "default";
public string ChannelId { get => _channelId; set => Set(ref _channelId, value); }
```

### - [ ] Step 5.4: Run tests to verify pass

Run: `dotnet test tests/Samwise.Tests --filter "FullyQualifiedName~AlarmSettingsTests"`
Expected: 3 passed.

### - [ ] Step 5.5: Commit

```bash
git add src/Samwise.Module/Alarms/AlarmSettings.cs tests/Samwise.Tests/Alarms/AlarmSettingsTests.cs
git commit -m "feat(samwise): StageAlarmRule gains Loop + ChannelId"
```

---

## Task 6: AlarmSettings.Channels + IPostLoadInit migration

**Files:**
- Modify: `src/Samwise.Module/Alarms/AlarmSettings.cs`
- Modify: `tests/Samwise.Tests/Alarms/AlarmSettingsTests.cs`

### - [ ] Step 6.1: Write failing tests

Append to `tests/Samwise.Tests/Alarms/AlarmSettingsTests.cs`:

```csharp
    [Fact]
    public void NewSettings_HasOneDefaultChannelInReplaceMode()
    {
        var s = new SamwiseSettings();
        s.Alarms.Channels.Should().HaveCount(1);
        s.Alarms.Channels[0].Id.Should().Be("default");
        s.Alarms.Channels[0].Name.Should().Be("Default");
        s.Alarms.Channels[0].Collision.Should().Be(AlarmCollisionBehavior.Replace);
    }

    [Fact]
    public void NewSettings_AllRulesRouteToDefaultChannel()
    {
        var s = new SamwiseSettings();
        foreach (var rule in s.Alarms.Rules.Values)
            rule.ChannelId.Should().Be("default");
    }

    [Fact]
    public void PostLoadInit_OldJsonWithoutChannels_InjectsDefaultChannel()
    {
        // Simulates old user JSON that predates the Channels property.
        const string oldJson = """
            {
              "alarms": {
                "enabled": true,
                "rules": {
                  "Ripe": { "enabled": true, "soundFilePath": null, "stopOnInteraction": true }
                }
              },
              "harvestedAutoClearMinutes": 10
            }
            """;

        var loaded = JsonSerializer.Deserialize(oldJson, SamwiseSettingsJsonContext.Default.SamwiseSettings)!;
        (loaded as Mithril.Shared.Settings.IPostLoadInit)?.PostLoadInit();

        loaded.Alarms.Channels.Should().NotBeEmpty();
        loaded.Alarms.Channels[0].Id.Should().Be("default");
        loaded.Alarms.Rules[PlotStage.Ripe].ChannelId.Should().Be("default");
    }

    [Fact]
    public void PostLoadInit_RuleWithDanglingChannelId_ReassignsToFirstChannel()
    {
        var s = new SamwiseSettings();
        s.Alarms.Rules[PlotStage.Ripe].ChannelId = "nonexistent-channel";

        (s as Mithril.Shared.Settings.IPostLoadInit)?.PostLoadInit();

        s.Alarms.Rules[PlotStage.Ripe].ChannelId.Should().Be(s.Alarms.Channels[0].Id);
    }
```

### - [ ] Step 6.2: Run tests to verify they fail

Run: `dotnet test tests/Samwise.Tests --filter "FullyQualifiedName~AlarmSettingsTests"`
Expected: compile fails (no `Channels` property on `AlarmSettings`, no `IPostLoadInit` on `SamwiseSettings`).

### - [ ] Step 6.3: Add `Channels` property to `AlarmSettings`

Open `src/Samwise.Module/Alarms/AlarmSettings.cs`. Add a field + property mirroring the existing `Rules` pattern (with event detach/attach on set):

```csharp
private List<AlarmChannel> _channels = DefaultChannels();
public List<AlarmChannel> Channels
{
    get => _channels;
    set
    {
        if (ReferenceEquals(_channels, value)) return;
        DetachChannelEvents(_channels);
        _channels = value ?? DefaultChannels();
        AttachChannelEvents(_channels);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Channels)));
    }
}

private void AttachChannelEvents(List<AlarmChannel> channels)
{
    foreach (var c in channels) c.PropertyChanged += OnChannelChanged;
}
private void DetachChannelEvents(List<AlarmChannel> channels)
{
    foreach (var c in channels) c.PropertyChanged -= OnChannelChanged;
}
private void OnChannelChanged(object? sender, PropertyChangedEventArgs e)
    => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Channels)));

private static List<AlarmChannel> DefaultChannels() => new()
{
    new AlarmChannel { Id = "default", Name = "Default", Collision = AlarmCollisionBehavior.Replace },
};
```

In the `AlarmSettings` constructor (currently just `AttachRuleEvents(_rules);`), add `AttachChannelEvents(_channels);` after the existing line.

### - [ ] Step 6.4: Implement `IPostLoadInit` on `AlarmSettings`

Add the interface to the class declaration:

```csharp
public sealed class AlarmSettings : INotifyPropertyChanged, Mithril.Shared.Settings.IPostLoadInit
```

Add the method:

```csharp
public void PostLoadInit()
{
    // STJ source-gen overwrites _channels from JSON if present; if the JSON
    // lacked the property the field initializer's DefaultChannels() stands.
    // The explicit empty check covers both "missing property" and "explicit []".
    if (_channels == null || _channels.Count == 0)
    {
        DetachChannelEvents(_channels ?? new());
        _channels = DefaultChannels();
        AttachChannelEvents(_channels);
    }

    // Re-attach event handlers on freshly-loaded children (STJ source-gen sets
    // the field without invoking AttachChannelEvents). Idempotent: detach first.
    DetachChannelEvents(_channels);
    AttachChannelEvents(_channels);
    DetachRuleEvents(_rules);
    AttachRuleEvents(_rules);

    // Dangling-ChannelId fixup: point any rule whose ChannelId doesn't
    // match a real channel at the first available channel.
    var validIds = new HashSet<string>(_channels.Select(c => c.Id), StringComparer.Ordinal);
    foreach (var rule in _rules.Values)
    {
        if (!validIds.Contains(rule.ChannelId))
            rule.ChannelId = _channels[0].Id;
    }
}
```

### - [ ] Step 6.5: Propagate `PostLoadInit` from `SamwiseSettings`

`SamwiseSettings` is the type registered with `JsonSettingsStore<T>`, so it's the one whose `PostLoadInit` actually fires. Add the interface and the fan-out method to `SamwiseSettings`:

```csharp
public sealed class SamwiseSettings : INotifyPropertyChanged, Mithril.Shared.Settings.IPostLoadInit
{
    // ... existing fields/props ...

    public void PostLoadInit()
    {
        _alarms.PostLoadInit();
        // Re-wire bubbling on freshly-loaded children (STJ source-gen path).
        _alarms.PropertyChanged -= OnAlarmsChanged;
        _alarms.PropertyChanged += OnAlarmsChanged;
        _calibration.PropertyChanged -= OnCalibrationChanged;
        _calibration.PropertyChanged += OnCalibrationChanged;
    }
}
```

### - [ ] Step 6.6: Register `AlarmChannel` with the JSON source-gen context

Find the `SamwiseSettingsJsonContext` at the bottom of `AlarmSettings.cs`:

```csharp
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SamwiseSettings))]
[JsonSerializable(typeof(CalibrationSettings))]
public partial class SamwiseSettingsJsonContext : JsonSerializerContext { }
```

Add `[JsonSerializable(typeof(AlarmChannel))]` and `[JsonSerializable(typeof(List<AlarmChannel>))]`:

```csharp
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SamwiseSettings))]
[JsonSerializable(typeof(CalibrationSettings))]
[JsonSerializable(typeof(AlarmChannel))]
[JsonSerializable(typeof(List<AlarmChannel>))]
public partial class SamwiseSettingsJsonContext : JsonSerializerContext { }
```

### - [ ] Step 6.7: Run tests to verify pass

Run: `dotnet test tests/Samwise.Tests --filter "FullyQualifiedName~AlarmSettingsTests"`
Expected: 7 passed (3 from Task 5 + 4 new).

### - [ ] Step 6.8: Commit

```bash
git add src/Samwise.Module/Alarms/AlarmSettings.cs tests/Samwise.Tests/Alarms/AlarmSettingsTests.cs
git commit -m "feat(samwise): AlarmSettings.Channels + IPostLoadInit migration"
```

---

## Task 7: AlarmSettings.MembershipSummary fan-out

**Files:**
- Modify: `src/Samwise.Module/Alarms/AlarmSettings.cs`
- Modify: `tests/Samwise.Tests/Alarms/AlarmSettingsTests.cs`

### - [ ] Step 7.1: Write failing tests

Append to `tests/Samwise.Tests/Alarms/AlarmSettingsTests.cs`:

```csharp
    [Fact]
    public void MembershipSummary_AfterPostLoadInit_ListsAssignedStages()
    {
        var s = new SamwiseSettings();
        (s as Mithril.Shared.Settings.IPostLoadInit)?.PostLoadInit();

        s.Alarms.Channels[0].MembershipSummary.Should().Contain("Ripe");
        s.Alarms.Channels[0].MembershipSummary.Should().Contain("Thirsty");
        s.Alarms.Channels[0].MembershipSummary.Should().Contain("NeedsFertilizer");
    }

    [Fact]
    public void MembershipSummary_RecomputesWhenRuleChannelIdChanges()
    {
        var s = new SamwiseSettings();
        var extra = new AlarmChannel { Name = "Extra", Collision = AlarmCollisionBehavior.Suppress };
        s.Alarms.Channels.Add(extra);
        (s as Mithril.Shared.Settings.IPostLoadInit)?.PostLoadInit();

        s.Alarms.Rules[PlotStage.Ripe].ChannelId = extra.Id;

        extra.MembershipSummary.Should().Contain("Ripe");
        s.Alarms.Channels[0].MembershipSummary.Should().NotContain("Ripe");
    }
```

### - [ ] Step 7.2: Run tests to verify they fail

Run: `dotnet test tests/Samwise.Tests --filter "FullyQualifiedName~AlarmSettingsTests"`
Expected: 2 new tests fail (MembershipSummary is empty).

### - [ ] Step 7.3: Add fan-out to `AlarmSettings`

In `AlarmSettings.cs`, add a private helper that walks rules and writes each channel's `MembershipSummary`:

```csharp
private void RecomputeMembershipSummaries()
{
    foreach (var channel in _channels)
    {
        var members = _rules
            .Where(kvp => kvp.Value.ChannelId == channel.Id)
            .Select(kvp => kvp.Key.ToString())
            .ToList();
        channel.MembershipSummary = members.Count == 0 ? "(empty)" : string.Join(" · ", members);
    }
}
```

Modify `OnRuleChanged` to call it when a rule's `ChannelId` changes. Today's `OnRuleChanged` doesn't see the property name; switch it to inspect `e.PropertyName`:

Replace:

```csharp
private void OnRuleChanged(object? sender, PropertyChangedEventArgs e)
    => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Rules)));
```

With:

```csharp
private void OnRuleChanged(object? sender, PropertyChangedEventArgs e)
{
    if (e.PropertyName == nameof(StageAlarmRule.ChannelId))
        RecomputeMembershipSummaries();
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Rules)));
}
```

At the end of `PostLoadInit`, call `RecomputeMembershipSummaries()` so initial state is correct.

### - [ ] Step 7.4: Run tests to verify pass

Run: `dotnet test tests/Samwise.Tests --filter "FullyQualifiedName~AlarmSettingsTests"`
Expected: 9 passed.

### - [ ] Step 7.5: Commit

```bash
git add src/Samwise.Module/Alarms/AlarmSettings.cs tests/Samwise.Tests/Alarms/AlarmSettingsTests.cs
git commit -m "feat(samwise): AlarmChannel.MembershipSummary recomputes on rule reassignment"
```

---

## Task 8: AlarmService — channel-resolved playback (Mix / Replace / Suppress / Loop)

**Files:**
- Modify: `src/Samwise.Module/Alarms/AlarmService.cs`
- Create: `tests/Samwise.Tests/Alarms/AlarmServiceTests.cs`

This task replaces the per-alarm `Dictionary<string, IPlaybackHandle>` with per-channel `Dictionary<string, List<ChannelOwner>>`, injects the `IAudioPlaybackSink`, and tests the four collision behaviors.

### - [ ] Step 8.1: Inject `IAudioPlaybackSink` into `AlarmService`

Open `src/Samwise.Module/Alarms/AlarmService.cs`. Modify the constructor:

```csharp
private readonly IAudioPlaybackSink _audio;

public AlarmService(GardenStateMachine state, SamwiseSettings settings, IAudioPlaybackSink audio)
{
    _state = state;
    _settings = settings;
    _audio = audio;
    _state.PlotChanged += OnPlotChanged;
}
```

Add `using Mithril.Shared.Audio;` if not already there.

### - [ ] Step 8.2: Replace `_playback` field with the per-channel list

Find:

```csharp
private readonly Dictionary<string, IPlaybackHandle> _playback = new(StringComparer.Ordinal);
```

Replace with:

```csharp
private sealed record ChannelOwner(string AlarmKey, IPlaybackHandle Handle);
private readonly Dictionary<string, List<ChannelOwner>> _channelPlayback = new(StringComparer.Ordinal);
```

### - [ ] Step 8.3: Add `ResolveChannel` helper

Add to the class:

```csharp
private AlarmChannel ResolveChannel(string id)
    => _settings.Alarms.Channels.FirstOrDefault(c => c.Id == id)
       ?? _settings.Alarms.Channels[0];

private static void PruneStopped(List<ChannelOwner> owners)
{
    for (int i = owners.Count - 1; i >= 0; i--)
        if (!owners[i].Handle.IsPlaying) owners.RemoveAt(i);
}

private List<ChannelOwner> OwnersOf(string channelId)
{
    if (!_channelPlayback.TryGetValue(channelId, out var list))
        _channelPlayback[channelId] = list = new();
    return list;
}
```

### - [ ] Step 8.4: Rewrite `Fire` to dispatch on `Collision`

Replace the existing `Fire` method:

```csharp
private void Fire(ActiveAlarm alarm, StageAlarmRule rule)
{
    Dispatch(() =>
    {
        var channel = ResolveChannel(rule.ChannelId);
        var owners = OwnersOf(channel.Id);
        PruneStopped(owners);

        bool willPlaySound;
        switch (channel.Collision)
        {
            case AlarmCollisionBehavior.Suppress:
                willPlaySound = owners.Count == 0;
                break;
            case AlarmCollisionBehavior.Replace:
                foreach (var o in owners) o.Handle.Stop();
                owners.Clear();
                willPlaySound = true;
                break;
            case AlarmCollisionBehavior.Mix:
            default:
                willPlaySound = true;
                break;
        }

        if (willPlaySound)
        {
            var handle = _audio.Play(rule.SoundFilePath, (float)_settings.Alarms.AlarmVolume, "samwise", loop: rule.Loop);
            owners.Add(new ChannelOwner(alarm.Key, handle));
        }

        if (_settings.Alarms.FlashWindow)
        {
            var win = Application.Current?.MainWindow;
            if (win is not null) WindowFlasher.Flash(win);
        }

        AlarmTriggered?.Invoke(this, alarm);
    });
}
```

### - [ ] Step 8.5: Adapt `OnPlotChanged` to call new `Fire` signature

Find the call site at the end of `OnPlotChanged`:

```csharp
Fire(new ActiveAlarm(key, e.Plot.CharName, e.Plot.CropType, DateTimeOffset.UtcNow), rule.SoundFilePath);
```

Replace with:

```csharp
Fire(new ActiveAlarm(key, e.Plot.CharName, e.Plot.CropType, DateTimeOffset.UtcNow), rule);
```

### - [ ] Step 8.6: Delete the now-obsolete `StopPlayback` and `StopAllPlayback`

These helpers operated on the old per-alarm `_playback` dictionary. Delete them. (Their callers are rewritten in Task 9.)

### - [ ] Step 8.7: Stub the methods that referenced `_playback` so the file builds

In `OnPlotChanged` (the stage-exit branch near the top), the existing code calls `StopPlayback(resolvedKey)`. Temporarily replace with a TODO call site that compiles — we'll fully rewrite in Task 9:

```csharp
// Stage-exit stop is rewritten in Task 9 to use _channelPlayback.
// Leaving the firedAt removal intact so existing dedup tests still pass.
```

Likewise `DismissAll`, `SnoozeAll`, `HandleHarvested`, and `Dispose` call the deleted helpers — replace each call with an empty body (the channel-aware version comes in Task 9):

```csharp
public void SnoozeAll()
{
    var until = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(_settings.Alarms.SnoozeMinutes);
    foreach (var key in _firedAt.Keys.ToArray()) _snoozedUntil[key] = until;
    _firedAt.Clear();
    // Channel-aware stop landed in Task 9.
}

public void DismissAll()
{
    _firedAt.Clear();
    // Channel-aware stop landed in Task 9.
}

public void HandleHarvested(Plot plot)
{
    var prefix = $"{plot.CharName}|{plot.PlotId}|";
    foreach (var k in _firedAt.Keys.Where(k => k.StartsWith(prefix)).ToArray())
        _firedAt.Remove(k);
    foreach (var k in _snoozedUntil.Keys.Where(k => k.StartsWith(prefix)).ToArray())
        _snoozedUntil.Remove(k);
    // Channel-aware stop landed in Task 9.
}

public void Dispose()
{
    _state.PlotChanged -= OnPlotChanged;
    // Channel-aware stop landed in Task 9.
}
```

### - [ ] Step 8.8: Update SamwiseModule DI to inject the sink

Open `src/Samwise.Module/SamwiseModule.cs`. The existing line:

```csharp
services.AddSingleton<AlarmService>();
```

Replace with an explicit factory so the sink dependency is wired:

```csharp
services.AddSingleton<AlarmService>(sp => new AlarmService(
    sp.GetRequiredService<GardenStateMachine>(),
    sp.GetRequiredService<SamwiseSettings>(),
    sp.GetRequiredService<Mithril.Shared.Audio.IAudioPlaybackSink>()));
```

### - [ ] Step 8.9: Write failing tests for Mix / Replace / Suppress / Loop

Create `tests/Samwise.Tests/Alarms/AlarmServiceTests.cs`:

```csharp
using FluentAssertions;
using Mithril.Shared.Audio;
using Samwise.Alarms;
using Samwise.Parsing;
using Samwise.State;
using Samwise.Tests;
using Xunit;

namespace Samwise.Tests.Alarms;

public class AlarmServiceTests
{
    private static readonly DateTime Base = new(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc);

    private sealed record Sut(
        AlarmService Service,
        FakeAudioPlaybackSink Sink,
        GardenStateMachine StateMachine,
        FakeTime Time,
        FakeActiveCharacterService ActiveChar,
        SamwiseSettings Settings);

    private static Sut BuildSut(AlarmCollisionBehavior collision = AlarmCollisionBehavior.Replace)
    {
        var cfg = new InMemoryCropConfig();
        var time = new FakeTime(Base);
        var ac = new FakeActiveCharacterService();
        var sm = new GardenStateMachine(cfg, time, activeChar: ac);
        ac.SetActiveCharacter("Hits", "");

        var settings = new SamwiseSettings();
        (settings as Mithril.Shared.Settings.IPostLoadInit)?.PostLoadInit();
        settings.Alarms.Channels[0].Collision = collision;
        settings.Alarms.Rules[PlotStage.Ripe].Enabled = true;
        settings.Alarms.Rules[PlotStage.Thirsty].Enabled = true;

        var sink = new FakeAudioPlaybackSink();
        var service = new AlarmService(sm, settings, sink);
        return new Sut(service, sink, sm, time, ac, settings);
    }

    /// <summary>
    /// Drive a plot from "no state" directly into Ripe by replaying the
    /// log-event sequence used by <c>GardenStateMachineTests.Tier1_StartInteraction</c>.
    /// </summary>
    private static void RipenPlot(Sut s, string plotId, string cropType)
    {
        s.StateMachine.Apply(new SetPetOwner(s.Time.Now.UtcDateTime, plotId));
        s.StateMachine.Apply(new AppearanceLoop(s.Time.Now.UtcDateTime, cropType));
        s.StateMachine.Apply(new UpdateDescription(
            s.Time.Now.UtcDateTime, plotId, cropType, "ripe", "Harvest " + cropType, 1.0));
    }

    /// <summary>
    /// Drive a plot into Thirsty by emitting the Water-Crop action.
    /// </summary>
    private static void ThirstyPlot(Sut s, string plotId, string cropType)
    {
        s.StateMachine.Apply(new SetPetOwner(s.Time.Now.UtcDateTime, plotId));
        s.StateMachine.Apply(new AppearanceLoop(s.Time.Now.UtcDateTime, cropType));
        s.StateMachine.Apply(new UpdateDescription(
            s.Time.Now.UtcDateTime, plotId, cropType, "", "Water " + cropType, 0.5));
    }

    [Fact]
    public void MixChannel_TwoPlotsRipen_BothHandlesPlay_NeitherStopped()
    {
        var s = BuildSut(AlarmCollisionBehavior.Mix);

        RipenPlot(s, "1", "Carrot");
        RipenPlot(s, "2", "Onion");

        s.Sink.Plays.Should().HaveCount(2);
        s.Sink.Plays[0].Handle.IsPlaying.Should().BeTrue();
        s.Sink.Plays[1].Handle.IsPlaying.Should().BeTrue();
    }

    [Fact]
    public void ReplaceChannel_SecondPlotRipens_StopsFirstHandle()
    {
        var s = BuildSut(AlarmCollisionBehavior.Replace);

        RipenPlot(s, "1", "Carrot");
        var firstHandle = s.Sink.Plays[0].Handle;
        RipenPlot(s, "2", "Onion");

        s.Sink.Plays.Should().HaveCount(2);
        firstHandle.IsPlaying.Should().BeFalse();             // Stop() was called
        s.Sink.Plays[1].Handle.IsPlaying.Should().BeTrue();
    }

    [Fact]
    public void SuppressChannel_SecondPlotRipens_DropsAudioButRaisesAlarm()
    {
        var s = BuildSut(AlarmCollisionBehavior.Suppress);
        var triggered = new List<ActiveAlarm>();
        s.Service.AlarmTriggered += (_, a) => triggered.Add(a);

        RipenPlot(s, "1", "Carrot");
        RipenPlot(s, "2", "Onion");

        s.Sink.Plays.Should().HaveCount(1);                   // second was suppressed
        s.Sink.Plays[0].Handle.IsPlaying.Should().BeTrue();
        triggered.Should().HaveCount(2);                      // visual still fired both times
    }

    [Fact]
    public void SuppressChannel_FirstHandleFinished_SecondAlarmPlays()
    {
        var s = BuildSut(AlarmCollisionBehavior.Suppress);

        RipenPlot(s, "1", "Carrot");
        s.Sink.Plays[0].Handle.IsPlaying = false;             // simulate playback finished
        RipenPlot(s, "2", "Onion");

        s.Sink.Plays.Should().HaveCount(2);
        s.Sink.Plays[1].Handle.IsPlaying.Should().BeTrue();
    }

    [Fact]
    public void LoopFlag_OnRule_IsPropagatedToSink()
    {
        var s = BuildSut(AlarmCollisionBehavior.Replace);
        s.Settings.Alarms.Rules[PlotStage.Ripe].Loop = true;

        RipenPlot(s, "1", "Carrot");

        s.Sink.Plays.Should().HaveCount(1);
        s.Sink.Plays[0].Loop.Should().BeTrue();
    }
}
```

Note: `FakeTime`, `InMemoryCropConfig`, and `FakeActiveCharacterService` already exist in the test project (see `tests/Samwise.Tests/GardenStateMachineTests.cs` and `tests/Samwise.Tests/FakeActiveCharacterService.cs`). They're either `internal` or accessible within the assembly — confirm and adjust accessibility if the new test file can't see them.

### - [ ] Step 8.10: Surface required test helpers if they aren't already accessible

If `FakeTime` or `InMemoryCropConfig` are `private` nested classes inside `GardenStateMachineTests`, lift them out to file-scoped `internal` classes in a new helper file `tests/Samwise.Tests/TestHelpers.cs` so the new test class can reuse them. Update `GardenStateMachineTests` to reference the lifted versions if you moved them.

If they're already `internal` at namespace scope, this step is a no-op.

### - [ ] Step 8.11: Run tests to verify they all pass

Run: `dotnet test tests/Samwise.Tests --filter "FullyQualifiedName~AlarmServiceTests"`
Expected: 5 passed.

### - [ ] Step 8.12: Commit

```bash
git add src/Samwise.Module/Alarms/AlarmService.cs src/Samwise.Module/SamwiseModule.cs tests/Samwise.Tests/Alarms/AlarmServiceTests.cs
git commit -m "feat(samwise): channel-resolved playback (Mix/Replace/Suppress) + Loop param

AlarmService now keys playback by channel id, holding a list of
(alarmKey, handle) owners per channel. Mix appends, Replace stops
existing and plays, Suppress skips audio but still fires visuals.
The rule's Loop flag flows through IAudioPlaybackSink.Play."
```

---

## Task 9: AlarmService — StopOnInteraction + HandleHarvested + Dismiss/Snooze

**Files:**
- Modify: `src/Samwise.Module/Alarms/AlarmService.cs`
- Modify: `tests/Samwise.Tests/Alarms/AlarmServiceTests.cs`

### - [ ] Step 9.1: Write failing tests

Append to `tests/Samwise.Tests/Alarms/AlarmServiceTests.cs`:

```csharp
    [Fact]
    public void StopOnInteraction_Replace_SecondPlotOwnsChannel_FirstResolveLeavesSecondPlaying()
    {
        var s = BuildSut(AlarmCollisionBehavior.Replace);
        s.Settings.Alarms.Rules[PlotStage.Ripe].StopOnInteraction = true;

        RipenPlot(s, "1", "Carrot");
        RipenPlot(s, "2", "Onion");
        var firstHandle = s.Sink.Plays[0].Handle;
        var secondHandle = s.Sink.Plays[1].Handle;

        // Resolve plot 1 (transition out of Ripe → Harvested via StartInteraction).
        s.StateMachine.Apply(new StartInteraction(s.Time.Now.UtcDateTime, "1", "SummonedCarrot"));

        firstHandle.IsPlaying.Should().BeFalse();   // already stopped by Replace
        secondHandle.IsPlaying.Should().BeTrue();   // plot 1's resolve must not stop it
    }

    [Fact]
    public void StopOnInteraction_Mix_OnlyOwnHandleStops()
    {
        var s = BuildSut(AlarmCollisionBehavior.Mix);
        s.Settings.Alarms.Rules[PlotStage.Ripe].StopOnInteraction = true;

        RipenPlot(s, "1", "Carrot");
        RipenPlot(s, "2", "Onion");
        var firstHandle = s.Sink.Plays[0].Handle;
        var secondHandle = s.Sink.Plays[1].Handle;

        // Resolve plot 1.
        s.StateMachine.Apply(new StartInteraction(s.Time.Now.UtcDateTime, "1", "SummonedCarrot"));

        firstHandle.IsPlaying.Should().BeFalse();   // plot 1's own handle stopped
        secondHandle.IsPlaying.Should().BeTrue();   // plot 2's still playing
    }

    [Fact]
    public void DismissAll_StopsEveryChannelOwner()
    {
        var s = BuildSut(AlarmCollisionBehavior.Mix);

        RipenPlot(s, "1", "Carrot");
        RipenPlot(s, "2", "Onion");

        s.Service.DismissAll();

        s.Sink.Plays.Should().AllSatisfy(p => p.Handle.IsPlaying.Should().BeFalse());
    }

    [Fact]
    public void SnoozeAll_StopsEveryChannelOwner_AndRecordsSnooze()
    {
        var s = BuildSut(AlarmCollisionBehavior.Mix);

        RipenPlot(s, "1", "Carrot");
        var firstHandle = s.Sink.Plays[0].Handle;

        s.Service.SnoozeAll();
        firstHandle.IsPlaying.Should().BeFalse();

        // Re-trigger the same Ripe transition for plot 1 — snooze must block the
        // second fire from playing audio (no new entry in Plays). We do this by
        // forcing a state change back into Ripe via a fresh AppearanceLoop + Update.
        RipenPlot(s, "1", "Carrot");
        s.Sink.Plays.Should().HaveCount(1);
    }

    [Fact]
    public void HandleHarvested_StopsAllOwnersForThatPlot()
    {
        var s = BuildSut(AlarmCollisionBehavior.Mix);
        s.Settings.Alarms.Rules[PlotStage.Ripe].StopOnInteraction = true;
        s.Settings.Alarms.Rules[PlotStage.Thirsty].StopOnInteraction = true;

        // Plot 1 enters Thirsty, then Ripe — two alarms on the same Mix channel.
        ThirstyPlot(s, "1", "Carrot");
        RipenPlot(s, "1", "Carrot");
        // Plot 2 stays Ripe to make sure HandleHarvested doesn't touch it.
        RipenPlot(s, "2", "Onion");

        var plot1Handles = s.Sink.Plays.Where(p => p.CallerId == "samwise").Take(2).Select(p => p.Handle).ToArray();
        var plot2Handle = s.Sink.Plays[^1].Handle;

        // Build a Plot DTO mirroring the in-state plot 1 to pass to HandleHarvested.
        var plot1 = s.StateMachine.Snapshot()["Hits"]["1"];

        s.Service.HandleHarvested(plot1);

        plot1Handles.Should().AllSatisfy(h => h.IsPlaying.Should().BeFalse());
        plot2Handle.IsPlaying.Should().BeTrue();
    }
```

Note on the `HandleHarvested` test: the assertion shape assumes plot 1's Thirsty → Ripe transition results in two separate `Play` calls (Thirsty's StopOnInteraction stops Thirsty when Ripe arrives on a Mix channel because the rule's `StopOnInteraction` fires). If the actual ordering produces only one handle for plot 1 in the sink list, simplify the assertion to track plot-1 handles by their `CallerId`/index rather than by count.

### - [ ] Step 9.2: Run tests to verify they fail

Run: `dotnet test tests/Samwise.Tests --filter "FullyQualifiedName~AlarmServiceTests"`
Expected: 5 new tests fail (current AlarmService bodies for Stop/Dismiss/Snooze/HandleHarvested are stubbed empty from Task 8).

### - [ ] Step 9.3: Implement StopOnInteraction for stage exit

In `OnPlotChanged`, find the existing stage-exit block at the top:

```csharp
if (e.OldStage is not null && e.NewStage != e.OldStage)
{
    var resolvedKey = $"{e.Plot.CharName}|{e.Plot.PlotId}|{e.OldStage}";
    if (_firedAt.Remove(resolvedKey)
        && _settings.Alarms.Rules.TryGetValue(e.OldStage.Value, out var oldRule)
        && oldRule.StopOnInteraction)
    {
        // Stage-exit stop is rewritten in Task 9 ...   ← from Task 8 placeholder
    }
}
```

Replace the placeholder with channel-aware per-owner stop:

```csharp
if (e.OldStage is not null && e.NewStage != e.OldStage)
{
    var resolvedKey = $"{e.Plot.CharName}|{e.Plot.PlotId}|{e.OldStage}";
    if (_firedAt.Remove(resolvedKey)
        && _settings.Alarms.Rules.TryGetValue(e.OldStage.Value, out var oldRule)
        && oldRule.StopOnInteraction)
    {
        StopOwner(oldRule.ChannelId, resolvedKey);
    }
}
```

Add the helper:

```csharp
private void StopOwner(string channelId, string alarmKey)
{
    var resolved = ResolveChannel(channelId).Id;
    if (!_channelPlayback.TryGetValue(resolved, out var owners)) return;
    var mine = owners.FirstOrDefault(o => o.AlarmKey == alarmKey);
    if (mine is not null)
    {
        mine.Handle.Stop();
        owners.Remove(mine);
    }
}
```

### - [ ] Step 9.4: Implement `HandleHarvested`

Replace the stub:

```csharp
public void HandleHarvested(Plot plot)
{
    var prefix = $"{plot.CharName}|{plot.PlotId}|";
    foreach (var k in _firedAt.Keys.Where(k => k.StartsWith(prefix)).ToArray())
    {
        _firedAt.Remove(k);
        var stageName = k[(prefix.Length)..];
        if (Enum.TryParse<PlotStage>(stageName, out var stage)
            && _settings.Alarms.Rules.TryGetValue(stage, out var rule)
            && rule.StopOnInteraction)
        {
            StopOwner(rule.ChannelId, k);
        }
    }
    foreach (var k in _snoozedUntil.Keys.Where(k => k.StartsWith(prefix)).ToArray())
        _snoozedUntil.Remove(k);
}
```

### - [ ] Step 9.5: Implement `DismissAll` / `SnoozeAll` / `Dispose`

```csharp
public void SnoozeAll()
{
    var until = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(_settings.Alarms.SnoozeMinutes);
    foreach (var key in _firedAt.Keys.ToArray()) _snoozedUntil[key] = until;
    _firedAt.Clear();
    StopAllChannelPlayback();
}

public void DismissAll()
{
    _firedAt.Clear();
    StopAllChannelPlayback();
}

public void Dispose()
{
    _state.PlotChanged -= OnPlotChanged;
    StopAllChannelPlayback();
}

private void StopAllChannelPlayback()
{
    foreach (var owners in _channelPlayback.Values)
    {
        foreach (var o in owners) o.Handle.Stop();
        owners.Clear();
    }
}
```

### - [ ] Step 9.6: Run tests to verify pass

Run: `dotnet test tests/Samwise.Tests --filter "FullyQualifiedName~AlarmServiceTests"`
Expected: 10 passed (5 from Task 8 + 5 new).

### - [ ] Step 9.7: Commit

```bash
git add src/Samwise.Module/Alarms/AlarmService.cs tests/Samwise.Tests/Alarms/AlarmServiceTests.cs
git commit -m "feat(samwise): channel-aware StopOnInteraction + Dismiss/Snooze/HandleHarvested"
```

---

## Task 10: Run the full test suite + manual smoke test

**No code changes. Verification step before UI work.**

### - [ ] Step 10.1: Full suite

Run: `dotnet test Mithril.slnx`
Expected: every test passes. Investigate any regression before continuing — a Gandalf or shell test could be sensitive to the `ConcurrentPlayback` default flip.

### - [ ] Step 10.2: Manual launch smoke

Run: `dotnet run --project src/Mithril.Shell`
Expected: app boots, Samwise tab loads, settings view renders without the "Allow concurrent alarm sounds" checkbox. No exceptions in the diagnostics log.

The full UI for channels lands in the next task; right now Settings still shows the existing per-stage cards (without Loop or Channel rows) but the underlying service is already channel-aware. The default channel + Replace behavior should make end-to-end behavior identical to pre-feature.

### - [ ] Step 10.3: No commit needed — verification step only.

---

## Task 11: SamwiseSettingsView — channels card section + per-stage Loop/Channel rows

**Files:**
- Modify: `src/Samwise.Module/Views/SamwiseSettingsView.xaml`
- Modify: `src/Samwise.Module/Views/SamwiseSettingsView.xaml.cs`

### - [ ] Step 11.1: Add the channels card section

Open `src/Samwise.Module/Views/SamwiseSettingsView.xaml`. Between the existing master-toggles block (Snooze duration, around line 53) and the "Per-stage rules" section header (line 56), insert:

```xml
<!-- Channels -->
<TextBlock Text="Channels" Foreground="#88FFFFFF" Margin="0,0,0,6" FontWeight="SemiBold"/>
<TextBlock Text="A channel groups stages with shared collision behavior. Within a channel, the rule decides what happens when a new alarm arrives while one is already playing. Across channels, sounds always mix."
           FontSize="{DynamicResource AppFontSizeHint}" Foreground="#88FFFFFF" TextWrapping="Wrap" Margin="0,0,0,8"/>

<ItemsControl ItemsSource="{Binding Alarms.Channels}">
    <ItemsControl.ItemTemplate>
        <DataTemplate>
            <Border Margin="0,0,0,6" Padding="10" CornerRadius="3"
                    Background="#FF252525" BorderBrush="#22FFFFFF" BorderThickness="1">
                <StackPanel>
                    <DockPanel LastChildFill="True">
                        <TextBlock DockPanel.Dock="Left" Text="Name:" VerticalAlignment="Center"
                                   Foreground="#FFE8E8E8" Margin="0,0,8,0"/>
                        <Button DockPanel.Dock="Right" Padding="8,4" Margin="6,0,0,0"
                                Click="DeleteChannel_Click" Tag="{Binding}"
                                ToolTip="Delete this channel">
                            <icon:PackIconLucide Kind="X" Width="12" Height="12"/>
                        </Button>
                        <ComboBox DockPanel.Dock="Right" Width="120" Margin="6,0"
                                  SelectedValue="{Binding Collision}" SelectedValuePath="Tag">
                            <ComboBoxItem Content="Mix" Tag="{x:Static alarms:AlarmCollisionBehavior.Mix}"/>
                            <ComboBoxItem Content="Replace" Tag="{x:Static alarms:AlarmCollisionBehavior.Replace}"/>
                            <ComboBoxItem Content="Suppress" Tag="{x:Static alarms:AlarmCollisionBehavior.Suppress}"/>
                        </ComboBox>
                        <TextBlock DockPanel.Dock="Right" Text="Behavior:" VerticalAlignment="Center"
                                   Foreground="#FFE8E8E8" Margin="6,0,4,0"/>
                        <TextBox Text="{Binding Name, UpdateSourceTrigger=LostFocus}" Padding="6,4"/>
                    </DockPanel>
                    <TextBlock Text="{Binding MembershipSummary, StringFormat=Contains: {0}}"
                               FontSize="{DynamicResource AppFontSizeHint}" Foreground="#66FFFFFF"
                               Margin="0,6,0,0" TextWrapping="Wrap"/>
                </StackPanel>
            </Border>
        </DataTemplate>
    </ItemsControl.ItemTemplate>
</ItemsControl>

<Button Click="AddChannel_Click" Padding="10,4" HorizontalAlignment="Left" Margin="0,4,0,24">
    <StackPanel Orientation="Horizontal">
        <icon:PackIconLucide Kind="Plus" Width="12" Height="12" VerticalAlignment="Center" Margin="0,0,4,0"/>
        <TextBlock Text="Add channel"/>
    </StackPanel>
</Button>
```

Add the `xmlns:alarms="clr-namespace:Samwise.Alarms"` namespace at the top of the UserControl tag for the `x:Static` references.

### - [ ] Step 11.2: Add Loop checkbox + Channel dropdown to each per-stage card

In the existing per-stage `DataTemplate` (lines 62-90 of the original file), inside the inner `<StackPanel>` and after the "Stop sound when resolved" `CheckBox`, append:

```xml
<CheckBox Margin="0,6,0,0" Foreground="#FFE8E8E8"
          Content="Loop until dismissed"
          IsChecked="{Binding Value.Loop}"
          IsEnabled="{Binding Value.Enabled}"/>
<DockPanel Margin="0,6,0,0" LastChildFill="False">
    <TextBlock Text="Channel:" Foreground="#FFE8E8E8" VerticalAlignment="Center" Width="80"/>
    <ComboBox Width="200" Margin="6,0"
              ItemsSource="{Binding DataContext.Alarms.Channels, RelativeSource={RelativeSource AncestorType=UserControl}}"
              DisplayMemberPath="Name"
              SelectedValuePath="Id"
              SelectedValue="{Binding Value.ChannelId}"
              IsEnabled="{Binding Value.Enabled}"/>
</DockPanel>
```

### - [ ] Step 11.3: Add the add/delete handlers in code-behind

Open `src/Samwise.Module/Views/SamwiseSettingsView.xaml.cs`. Add the handler methods to the class:

```csharp
private void AddChannel_Click(object sender, RoutedEventArgs e)
{
    if (DataContext is not SamwiseSettings s) return;
    var next = $"Channel {s.Alarms.Channels.Count + 1}";
    s.Alarms.Channels.Add(new AlarmChannel { Name = next, Collision = AlarmCollisionBehavior.Mix });
    // Bound ItemsControl picks up the change because Channels is a List<T>
    // and AlarmSettings raises PropertyChanged(nameof(Channels)) via the
    // attached channel-event fan-out. If the list-mutation doesn't refresh
    // the ItemsControl in practice, swap List<T> → ObservableCollection<T>
    // — but defer that until we see the issue.
}

private void DeleteChannel_Click(object sender, RoutedEventArgs e)
{
    if (DataContext is not SamwiseSettings s) return;
    if (sender is not FrameworkElement fe || fe.Tag is not AlarmChannel channel) return;
    if (s.Alarms.Channels.Count <= 1) return; // never delete the last channel

    var fallbackId = s.Alarms.Channels.First(c => c.Id != channel.Id).Id;
    foreach (var rule in s.Alarms.Rules.Values)
    {
        if (rule.ChannelId == channel.Id)
            rule.ChannelId = fallbackId;
    }
    s.Alarms.Channels.Remove(channel);
}
```

Add `using Samwise.Alarms;` and `using System.Linq;` if not already present.

### - [ ] Step 11.4: Manually verify the UI works

Run: `dotnet run --project src/Mithril.Shell`

Walk through:
1. Open the Samwise settings tab. The new "Channels" section shows one row: Default / Replace / Contains: Ripe · Thirsty · NeedsFertilizer.
2. Toggle one of the per-stage `Loop` checkboxes; trigger that alarm (test sound button); verify it loops.
3. Click "Add channel" — a new "Channel 2" / Mix row appears.
4. Reassign Ripe's Channel dropdown to "Channel 2". The Default channel's Contains line drops Ripe; Channel 2's gains Ripe.
5. Delete Channel 2. Ripe reassigns back to Default (verify the dropdown reflects "Default" again).
6. Try to delete the last remaining channel — button click should no-op silently.

### - [ ] Step 11.5: Commit

```bash
git add src/Samwise.Module/Views/SamwiseSettingsView.xaml src/Samwise.Module/Views/SamwiseSettingsView.xaml.cs
git commit -m "feat(samwise): settings UI — channels card section + per-stage Loop/Channel rows"
```

---

## Task 12: Final smoke + open the PR

### - [ ] Step 12.1: Full test sweep

Run: `dotnet test Mithril.slnx`
Expected: all green.

### - [ ] Step 12.2: Hands-on UAT scenarios

For each, run `dotnet run --project src/Mithril.Shell`, then in-game (or via a synthetic Player.log replay if you have one) trigger:

1. **Single Ripe transition, default Replace channel, Loop off:** plays the sound once. ✓
2. **Single Ripe transition, Loop on:** sound loops until you `StopAllSounds` (default `mithril.stop-all-sounds` hotkey) or the plot exits Ripe.
3. **Cluster of 5 Ripes within 2 seconds, default Replace channel:** rapid sound replacement (cacophony — this is the *un-fixed* case; user opts into Suppress to fix).
4. **Same cluster, Channel collision = Suppress:** one sound fires; subsequent ones are silent.
5. **Same cluster, Suppress + Loop:** first plot's loop starts; subsequent ones suppressed; loop covers the whole cluster. *This is the gold-path of the whole feature.*
6. **Two channels (Ripe on A=Suppress, Thirsty on B=Mix):** Ripe alarms collapse to one; Thirsty alarms can layer with the Ripe loop.

### - [ ] Step 12.3: Open the PR

```bash
git push -u origin feat/samwise-alarm-channels
gh pr create --title "Samwise: per-stage Loop, alarm channels, Mix/Replace/Suppress collision" --body "$(cat <<'EOF'
## Summary
- Per-stage `Loop` flag + per-stage `ChannelId` on `StageAlarmRule`.
- New `AlarmChannel { Mix | Replace | Suppress }` with user-named groupings; default channel migrates to Replace to preserve existing behavior, new channels default to Mix.
- `AudioPlayer.ConcurrentPlayback` flipped to `true`; user-facing "Allow concurrent alarm sounds" checkbox removed from both Samwise and Gandalf settings.
- Module audio is now isolated — Gandalf and Samwise no longer interrupt each other.

Design notebook: [docs/agent-plans/2026-05-11-samwise-alarm-channels.md](docs/agent-plans/2026-05-11-samwise-alarm-channels.md)
Implementation plan: [docs/superpowers/plans/2026-05-11-samwise-alarm-channels.md](docs/superpowers/plans/2026-05-11-samwise-alarm-channels.md)

Related: #208 (schema versioning for module-wide settings — orthogonal follow-up).

## Test plan
- [x] Unit tests for `AlarmChannel`, `AlarmSettings` (defaults + migration + MembershipSummary), `AlarmService` (Mix/Replace/Suppress/Loop/StopOnInteraction/HandleHarvested/Dismiss/Snooze).
- [ ] Manual: cluster of 5 Ripens with Suppress + Loop fires exactly one looping sound.
- [ ] Manual: two channels with different policies operate independently.
- [ ] Manual: Gandalf timer alarm + Samwise ripe alarm don't interrupt each other.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

---

## Notes for the implementer

- **Tests for the GardenStateMachine wiring**: the existing `GardenStateMachineTests.cs` has the canonical Plant + ripen helpers. Copy them into `AlarmServiceTests.cs` rather than reaching across test files — the harness is small enough that duplication is cheaper than refactoring shared helpers.
- **WPF dispatcher behavior in tests**: `AlarmService.Dispatch` falls back to synchronous execution when `Application.Current` is null. Tests don't need to spin up a WPF app; the call runs on the test thread.
- **`Application.Current?.MainWindow`** in `Fire` will be null in tests — the `if (win is not null)` guard short-circuits the flash call, so tests can leave `FlashWindow` at its default.
- **`List<AlarmChannel>` vs `ObservableCollection<AlarmChannel>`**: the plan uses `List<T>` to match the existing settings pattern. If add/delete operations don't refresh the bound `ItemsControl` in practice (because List doesn't notify), swap to `ObservableCollection<T>` and update the JSON context. Try List first; only escalate on failure.
- **`Migrate` field initializer behavior**: the spec relies on STJ source-gen's "field initializer runs in parameterless ctor, JSON property assignments happen after" semantics. If a Samwise test reveals that an old-JSON load lands with `Channels = null` instead of `Channels = DefaultChannels()`, that means STJ behavior is different — the `PostLoadInit` empty-list check handles both cases, so this is a belt-and-suspenders concern.

