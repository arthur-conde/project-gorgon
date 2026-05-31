using System;
using System.IO;
using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Mithril.Shared.Settings;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class MapCaptureRegionRoundTripTests
{
    [Fact]
    public void Bbox_round_trips_through_the_store()
    {
        var path = Path.Combine(Path.GetTempPath(), "mithril-capture-" + Guid.NewGuid() + ".json");
        try
        {
            var store = new JsonSettingsStore<MapCaptureSettings>(path, MapCaptureSettingsJsonContext.Default.MapCaptureSettings);
            var provider = new MapCaptureRegionProvider(store, store.Load());
            provider.Set(new CaptureRect(120, 80, 800, 600));

            var reloaded = new MapCaptureRegionProvider(store, store.Load());
            reloaded.Current.Should().Be(new CaptureRect(120, 80, 800, 600));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Current_is_null_before_any_bbox_is_set()
    {
        var store = new JsonSettingsStore<MapCaptureSettings>(
            Path.Combine(Path.GetTempPath(), "mithril-capture-" + Guid.NewGuid() + ".json"),
            MapCaptureSettingsJsonContext.Default.MapCaptureSettings);
        new MapCaptureRegionProvider(store, store.Load()).Current.Should().BeNull();
    }
}
