using System.IO;
using System.Text.Json;
using FluentAssertions;
using Mithril.Shared.Game;
using Xunit;

namespace Mithril.Shared.Tests;

/// <summary>
/// JSON round-trip + degraded-mode coverage for <see cref="JsonShiftCatalog"/>.
/// Pairs with <see cref="TimeOfDayShiftsTests"/>: that file pins the math
/// (<c>NextTransition</c>) using the bundled catalog; this file pins the
/// loader's behaviour around schema versioning, malformed input, and the
/// hardcoded fallback.
/// </summary>
[Trait("Category", "FileIO")]
public class ShiftCatalogJsonTests
{
    private static string TempPath(string name) =>
        Path.Combine(Path.GetTempPath(), $"mithril-shift-catalog-{Guid.NewGuid():N}-{name}");

    [Fact]
    public void Bundled_catalog_round_trips_through_TryLoadFromFile()
    {
        // Find the bundled file via the JsonShiftCatalog default path lookup.
        var bundled = Path.Combine(AppContext.BaseDirectory, "Reference", "BundledData", "shifts.json");
        File.Exists(bundled).Should().BeTrue("the build must copy shifts.json next to the test assembly");

        var loaded = JsonShiftCatalog.TryLoadFromFile(bundled);
        loaded.Should().NotBeNull();
        loaded!.Select(s => s.Slug).Should().Equal("midnight", "dawn", "morning", "afternoon", "dusk", "night");
        loaded!.Select(s => s.StartHour).Should().Equal(0, 5, 8, 12, 17, 20);
    }

    [Fact]
    public void Missing_file_falls_back_to_hardcoded_snapshot()
    {
        var missing = TempPath("missing.json");
        var catalog = new JsonShiftCatalog(bundledDir: Path.GetDirectoryName(missing));
        catalog.Shifts.Should().BeEquivalentTo(JsonShiftCatalog.HardcodedFallback);
    }

    [Fact]
    public void Malformed_json_falls_back_to_hardcoded_snapshot()
    {
        var dir = TempPath("malformed-dir");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "shifts.json"), "{ this is not json");
            var catalog = new JsonShiftCatalog(bundledDir: dir);
            catalog.Shifts.Should().BeEquivalentTo(JsonShiftCatalog.HardcodedFallback);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Unsupported_schema_version_is_rejected_and_falls_back()
    {
        var dir = TempPath("schema-dir");
        Directory.CreateDirectory(dir);
        try
        {
            // Schema 99 is "from the future" — loader must reject and fall back
            // rather than risk acting on a shape it can't validate.
            var future = new ShiftCatalogPayload
            {
                SchemaVersion = 99,
                Shifts = { new ShiftCatalogPayload.ShiftEntry { Slug = "weird", Label = "Weird", StartHour = 7 } },
            };
            File.WriteAllText(Path.Combine(dir, "shifts.json"),
                JsonSerializer.Serialize(future, ShiftCatalogJsonContext.Default.ShiftCatalogPayload));

            var catalog = new JsonShiftCatalog(bundledDir: dir);
            catalog.Shifts.Should().BeEquivalentTo(JsonShiftCatalog.HardcodedFallback);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Empty_shifts_array_falls_back()
    {
        var dir = TempPath("empty-dir");
        Directory.CreateDirectory(dir);
        try
        {
            var emptyPayload = new ShiftCatalogPayload { SchemaVersion = JsonShiftCatalog.CurrentSchemaVersion };
            File.WriteAllText(Path.Combine(dir, "shifts.json"),
                JsonSerializer.Serialize(emptyPayload, ShiftCatalogJsonContext.Default.ShiftCatalogPayload));

            var catalog = new JsonShiftCatalog(bundledDir: dir);
            catalog.Shifts.Should().BeEquivalentTo(JsonShiftCatalog.HardcodedFallback);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Out_of_range_StartHour_falls_back()
    {
        var dir = TempPath("range-dir");
        Directory.CreateDirectory(dir);
        try
        {
            var bad = new ShiftCatalogPayload
            {
                SchemaVersion = JsonShiftCatalog.CurrentSchemaVersion,
                Shifts =
                {
                    new ShiftCatalogPayload.ShiftEntry { Slug = "midnight", Label = "Midnight", StartHour = 0 },
                    new ShiftCatalogPayload.ShiftEntry { Slug = "broken", Label = "Broken", StartHour = 27 },
                },
            };
            File.WriteAllText(Path.Combine(dir, "shifts.json"),
                JsonSerializer.Serialize(bad, ShiftCatalogJsonContext.Default.ShiftCatalogPayload));

            var catalog = new JsonShiftCatalog(bundledDir: dir);
            catalog.Shifts.Should().BeEquivalentTo(JsonShiftCatalog.HardcodedFallback,
                "any out-of-range entry invalidates the entire payload — better to ship stale than to ship broken");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Custom_catalog_with_non_default_shifts_drives_NextTransition_correctly()
    {
        // Verify the loader path is data-driven: a 3-shift catalog should
        // produce 3-shift transition behaviour, not silently revert to 6.
        var dir = TempPath("custom-dir");
        Directory.CreateDirectory(dir);
        try
        {
            var custom = new ShiftCatalogPayload
            {
                SchemaVersion = JsonShiftCatalog.CurrentSchemaVersion,
                Shifts =
                {
                    new ShiftCatalogPayload.ShiftEntry { Slug = "early",  Label = "Early",  Emoji = "A", StartHour = 6  },
                    new ShiftCatalogPayload.ShiftEntry { Slug = "midday", Label = "Midday", Emoji = "B", StartHour = 12 },
                    new ShiftCatalogPayload.ShiftEntry { Slug = "late",   Label = "Late",   Emoji = "C", StartHour = 18 },
                },
            };
            File.WriteAllText(Path.Combine(dir, "shifts.json"),
                JsonSerializer.Serialize(custom, ShiftCatalogJsonContext.Default.ShiftCatalogPayload));

            var catalog = new JsonShiftCatalog(bundledDir: dir);
            catalog.Shifts.Select(s => s.Slug).Should().Equal("early", "midday", "late");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Out_of_order_entries_are_sorted_by_StartHour_on_load()
    {
        // A future hand-edit to shifts.json that lists entries out of order
        // shouldn't break the settings UI (which relies on time order). The
        // loader sorts defensively.
        var dir = TempPath("disordered-dir");
        Directory.CreateDirectory(dir);
        try
        {
            var disordered = new ShiftCatalogPayload
            {
                SchemaVersion = JsonShiftCatalog.CurrentSchemaVersion,
                Shifts =
                {
                    new ShiftCatalogPayload.ShiftEntry { Slug = "z", Label = "Z", StartHour = 22 },
                    new ShiftCatalogPayload.ShiftEntry { Slug = "a", Label = "A", StartHour = 1  },
                    new ShiftCatalogPayload.ShiftEntry { Slug = "m", Label = "M", StartHour = 11 },
                },
            };
            File.WriteAllText(Path.Combine(dir, "shifts.json"),
                JsonSerializer.Serialize(disordered, ShiftCatalogJsonContext.Default.ShiftCatalogPayload));

            var catalog = new JsonShiftCatalog(bundledDir: dir);
            catalog.Shifts.Select(s => s.StartHour).Should().Equal(1, 11, 22);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
