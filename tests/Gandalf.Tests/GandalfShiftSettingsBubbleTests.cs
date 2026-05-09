using System.IO;
using FluentAssertions;
using Gandalf.Domain;
using Mithril.Shared.Settings;
using Xunit;

namespace Gandalf.Tests;

/// <summary>
/// Regression net for #179. The bug: toggling a shift's Enabled checkbox
/// fired PropertyChanged on the child ShiftAlarmConfig only, never on the
/// parent GandalfShiftSettings, so SettingsAutoSaver (which only subscribes
/// to the root) never marked dirty and shifts.json was never rewritten.
/// </summary>
public class GandalfShiftSettingsBubbleTests
{
    [Fact]
    public void Toggling_Enabled_on_a_GetOrCreate_child_fires_root_PropertyChanged()
    {
        var settings = new GandalfShiftSettings();
        var fired = 0;
        settings.PropertyChanged += (_, _) => fired++;

        var config = settings.GetOrCreate("midnight");
        // GetOrCreate fires once for the structural change (a new slug entry).
        var beforeToggle = fired;

        config.Enabled = true;

        (fired - beforeToggle).Should().Be(1,
            "the child's PropertyChanged must bubble to the parent so the autosaver wakes up");
    }

    [Fact]
    public void Toggling_SoundFilePath_on_a_GetOrCreate_child_fires_root_PropertyChanged()
    {
        var settings = new GandalfShiftSettings();
        var config = settings.GetOrCreate("dawn");

        var fired = 0;
        settings.PropertyChanged += (_, _) => fired++;

        config.SoundFilePath = "C:/sounds/bell.wav";

        fired.Should().Be(1);
    }

    [Fact]
    public void Loaded_children_bubble_after_PostLoadInit()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"mithril-shifts-test-{Guid.NewGuid():N}.json");
        try
        {
            // Seed: a settings instance with one shift entry, saved through
            // the production store path.
            var seed = new GandalfShiftSettings();
            var seeded = seed.GetOrCreate("morning");
            seeded.Enabled = true;
            var store = new JsonSettingsStore<GandalfShiftSettings>(
                tempPath, GandalfShiftSettingsJsonContext.Default.GandalfShiftSettings);
            store.Save(seed);

            // Reload — STJ source-gen populates ByShiftSlug directly,
            // bypassing GetOrCreate. Without PostLoadInit (invoked by
            // JsonSettingsStore.Load), the loaded child wouldn't bubble.
            var loaded = store.Load();
            loaded.ByShiftSlug.Should().ContainKey("morning");

            var fired = 0;
            loaded.PropertyChanged += (_, _) => fired++;

            loaded.ByShiftSlug["morning"].Enabled = false;

            fired.Should().Be(1,
                "PostLoadInit must re-wire bubbling on every deserialized ShiftAlarmConfig");
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void GetOrCreate_is_idempotent_and_does_not_double_subscribe()
    {
        var settings = new GandalfShiftSettings();
        var first = settings.GetOrCreate("dusk");
        var second = settings.GetOrCreate("dusk");

        second.Should().BeSameAs(first);

        var fired = 0;
        settings.PropertyChanged += (_, _) => fired++;

        first.Enabled = true;

        fired.Should().Be(1, "the second GetOrCreate must not have re-subscribed");
    }
}
