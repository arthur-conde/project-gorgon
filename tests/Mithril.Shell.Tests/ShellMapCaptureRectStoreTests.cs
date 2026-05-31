using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Mithril.Shared.Settings;
using Mithril.Shell;
using Xunit;

namespace Mithril.Shell.Tests;

/// <summary>
/// #947: <see cref="ShellMapCaptureRectStore"/> backs the Capture-defined
/// <see cref="IMapCaptureRectStore"/> seam with the persisted
/// <see cref="ShellSettings.MapCaptureBbox"/>. The store must round-trip a snipped
/// rect (in physical pixels), flush it to disk on <see cref="IMapCaptureRectStore.Set"/>
/// (ShellSettings has no autosaver — it's saved explicitly), and report null when no
/// rect has been snipped.
/// </summary>
public sealed class ShellMapCaptureRectStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mithril-947-" + Guid.NewGuid().ToString("N"));

    public ShellMapCaptureRectStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Get_is_null_when_no_bbox_has_been_persisted()
    {
        var store = new ShellMapCaptureRectStore(new ShellSettings(), new RecordingStore());
        store.Get().Should().BeNull();
    }

    [Fact]
    public void Set_then_Get_round_trips()
    {
        var settings = new ShellSettings();
        var store = new ShellMapCaptureRectStore(settings, new RecordingStore());

        store.Set(new CaptureRect(-200, 50, 640, 480));

        store.Get().Should().Be(new CaptureRect(-200, 50, 640, 480));
        settings.MapCaptureBbox.Should().NotBeNull();
        settings.MapCaptureBbox!.Left.Should().Be(-200);
        settings.MapCaptureBbox.Width.Should().Be(640);
    }

    [Fact]
    public void Set_flushes_to_the_settings_store()
    {
        var recording = new RecordingStore();
        var store = new ShellMapCaptureRectStore(new ShellSettings(), recording);

        store.Set(new CaptureRect(10, 20, 30, 40));

        recording.SaveCount.Should().Be(1, "Set must persist immediately — ShellSettings has no autosaver");
    }

    [Fact]
    public void Set_persists_through_a_real_json_store_and_survives_reload()
    {
        var path = Path.Combine(_dir, "shell.json");
        var settings = new ShellSettings();
        var jsonStore = new JsonSettingsStore<ShellSettings>(path, ShellSettingsJsonContext.Default.ShellSettings);

        var store = new ShellMapCaptureRectStore(settings, jsonStore);
        store.Set(new CaptureRect(100, 200, 300, 400));

        File.Exists(path).Should().BeTrue();

        // A fresh load (new shell launch) sees the persisted bbox.
        var reloaded = jsonStore.Load();
        reloaded.MapCaptureBbox.Should().NotBeNull();
        reloaded.MapCaptureBbox!.Left.Should().Be(100);
        reloaded.MapCaptureBbox.Top.Should().Be(200);
        reloaded.MapCaptureBbox.Width.Should().Be(300);
        reloaded.MapCaptureBbox.Height.Should().Be(400);
    }

    [Fact]
    public void Absent_bbox_key_loads_as_null_no_migration_needed()
    {
        // A pre-#947 settings file with no mapCaptureBbox key → null on load.
        var path = Path.Combine(_dir, "shell.json");
        File.WriteAllText(path, """{ "gameRoot": "C:\\PG" }""");
        var jsonStore = new JsonSettingsStore<ShellSettings>(path, ShellSettingsJsonContext.Default.ShellSettings);

        var loaded = jsonStore.Load();

        loaded.MapCaptureBbox.Should().BeNull();
        new ShellMapCaptureRectStore(loaded, jsonStore).Get().Should().BeNull();
    }

    private sealed class RecordingStore : ISettingsStore<ShellSettings>
    {
        public int SaveCount { get; private set; }
        public string FilePath => "(memory)";
        public ShellSettings Load() => new();
        public Task<ShellSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(new ShellSettings());
        public Task SaveAsync(ShellSettings value, CancellationToken ct = default) { SaveCount++; return Task.CompletedTask; }
        public void Save(ShellSettings value) => SaveCount++;
    }
}
