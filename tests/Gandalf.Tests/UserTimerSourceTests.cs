using System.IO;
using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Mithril.Shared.Character;
using Mithril.Shared.Settings;
using Xunit;

namespace Gandalf.Tests;

[Trait("Category", "FileIO")]
[Collection("FileIO")]
public class UserTimerSourceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _defsPath;
    private readonly string _charactersDir;

    public UserTimerSourceTests()
    {
        _dir = Mithril.TestSupport.TestPaths.CreateTempDir("gandalf_user_source");
        _defsPath = Path.Combine(_dir, "definitions.json");
        _charactersDir = Path.Combine(_dir, "characters");
        Directory.CreateDirectory(_charactersDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private (UserTimerSource source, TimerDefinitionsService defs, TimerProgressService progress, FakeActiveCharacterService active)
        BuildServices()
    {
        var defStore = new JsonSettingsStore<GandalfDefinitions>(_defsPath,
            GandalfDefinitionsJsonContext.Default.GandalfDefinitions);
        var defs = defStore.Load();
        var defsSvc = new TimerDefinitionsService(defStore, defs);

        var active = new FakeActiveCharacterService();
        var store = new PerCharacterStore<GandalfProgress>(_charactersDir, "gandalf.json",
            GandalfProgressJsonContext.Default.GandalfProgress);
        var view = new PerCharacterView<GandalfProgress>(active, store);
        var progressSvc = new TimerProgressService(view, defsSvc,
            new PerCharacterStoreOptions { CharactersRootDir = _charactersDir });

        var source = new UserTimerSource(defsSvc, progressSvc);
        return (source, defsSvc, progressSvc, active);
    }

    [Fact]
    public void SourceId_is_stable_wire_identifier()
    {
        var (source, defs, progress, _) = BuildServices();
        try
        {
            source.SourceId.Should().Be("gandalf.user");
        }
        finally
        {
            source.Dispose(); progress.Dispose(); defs.Dispose();
        }
    }

    [Fact]
    public void Catalog_projects_definitions_with_groupkey_as_region()
    {
        var (source, defs, progress, _) = BuildServices();
        try
        {
            defs.Add(new GandalfTimerDef
            {
                Name = "Chest", Duration = TimeSpan.FromHours(3),
                Region = "Goblin Dungeon", Map = "Lower",
            });

            source.Catalog.Should().HaveCount(1);
            source.Catalog[0].DisplayName.Should().Be("Chest");
            source.Catalog[0].Region.Should().Be("Goblin Dungeon > Lower");
            source.Catalog[0].Duration.Should().Be(TimeSpan.FromHours(3));
            source.Catalog[0].SourceMetadata.Should().BeOfType<GandalfTimerDef>();
        }
        finally
        {
            source.Dispose(); progress.Dispose(); defs.Dispose();
        }
    }

    [Fact]
    public void Idle_timer_has_no_progress_entry()
    {
        var (source, defs, progress, active) = BuildServices();
        try
        {
            active.SetActiveCharacter("Arthur", "Kwatoxi");
            defs.Add(new GandalfTimerDef { Name = "Chest", Duration = TimeSpan.FromHours(1) });

            source.Progress.Should().BeEmpty();
        }
        finally
        {
            source.Dispose(); progress.Dispose(); defs.Dispose();
        }
    }

    [Fact]
    public void Started_timer_appears_in_progress_with_started_at()
    {
        var (source, defs, progress, active) = BuildServices();
        try
        {
            active.SetActiveCharacter("Arthur", "Kwatoxi");
            defs.Add(new GandalfTimerDef { Name = "Chest", Duration = TimeSpan.FromHours(1) });
            var id = defs.Definitions[0].Id;

            progress.Start(id);

            source.Progress.Should().ContainKey(id);
            source.Progress[id].StartedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2));
            source.Progress[id].DismissedAt.Should().BeNull();
        }
        finally
        {
            source.Dispose(); progress.Dispose(); defs.Dispose();
        }
    }

    [Fact]
    public void DefinitionsChanged_raises_CatalogChanged()
    {
        var (source, defs, progress, _) = BuildServices();
        try
        {
            var fired = 0;
            source.CatalogChanged += (_, _) => fired++;

            defs.Add(new GandalfTimerDef { Name = "Chest", Duration = TimeSpan.FromHours(1) });

            fired.Should().Be(1);
        }
        finally
        {
            source.Dispose(); progress.Dispose(); defs.Dispose();
        }
    }

    [Fact]
    public void TimerExpired_bridges_to_TimerReady_with_source_id()
    {
        var (source, defs, progress, active) = BuildServices();
        try
        {
            active.SetActiveCharacter("Arthur", "Kwatoxi");
            defs.Add(new GandalfTimerDef { Name = "Chest", Duration = TimeSpan.FromMilliseconds(1) });
            var id = defs.Definitions[0].Id;

            var captured = new List<TimerReadyEventArgs>();
            source.TimerReady += (_, e) => captured.Add(e);

            progress.Start(id);
            // Definitively past-due — well above the 1ms duration, no flake risk.
            System.Threading.Thread.Sleep(20);
            progress.CheckExpirations();

            captured.Should().HaveCount(1);
            captured[0].SourceId.Should().Be("gandalf.user");
            captured[0].Key.Should().Be(id);
            captured[0].DisplayName.Should().Be("Chest");
            captured[0].SourceMetadata.Should().BeOfType<GandalfTimerDef>();
        }
        finally
        {
            source.Dispose(); progress.Dispose(); defs.Dispose();
        }
    }

    [Fact]
    public void Adding_a_definition_emits_RowsChanged_with_added_delta()
    {
        var (source, defs, progress, active) = BuildServices();
        try
        {
            active.SetActiveCharacter("Arthur", "Kwatoxi");

            var batches = new List<IReadOnlyList<TimerRowDelta>>();
            source.RowsChanged += (_, e) => batches.Add(e.Deltas);

            defs.Add(new GandalfTimerDef
            {
                Name = "Chest", Duration = TimeSpan.FromHours(3),
                Region = "Goblin Dungeon", Map = "Lower",
            });

            batches.Should().ContainSingle();
            batches[0].Should().ContainSingle(d => d.Kind == TimerRowChangeKind.Added);
        }
        finally
        {
            source.Dispose(); progress.Dispose(); defs.Dispose();
        }
    }

    [Fact]
    public void Starting_a_timer_emits_RowsChanged_with_progress_changed_delta()
    {
        var (source, defs, progress, active) = BuildServices();
        try
        {
            active.SetActiveCharacter("Arthur", "Kwatoxi");

            defs.Add(new GandalfTimerDef
            {
                Name = "Chest", Duration = TimeSpan.FromHours(3),
                Region = "Goblin Dungeon", Map = "Lower",
            });
            var id = defs.Definitions[0].Id;

            var batches = new List<IReadOnlyList<TimerRowDelta>>();
            source.RowsChanged += (_, e) => batches.Add(e.Deltas);

            progress.Start(id);

            batches.SelectMany(b => b)
                .Should().ContainSingle(d =>
                    d.Key == id && d.Kind == TimerRowChangeKind.ProgressChanged);
        }
        finally
        {
            source.Dispose(); progress.Dispose(); defs.Dispose();
        }
    }
}
