using System.IO;
using System.Text.Json;
using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Gorgon.Shared.Character;
using Gorgon.Shared.Settings;
using Xunit;

namespace Gandalf.Tests;

public class TimerServicesTests : IDisposable
{
    private readonly string _dir;
    private readonly string _defsPath;
    private readonly string _charactersDir;

    public TimerServicesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"gandalf_svc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _defsPath = Path.Combine(_dir, "definitions.json");
        _charactersDir = Path.Combine(_dir, "characters");
        Directory.CreateDirectory(_charactersDir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (TimerDefinitionsService defs, TimerProgressService progress, PerCharacterView<GandalfProgress> view, FakeActiveCharacterService active)
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
        var progressSvc = new TimerProgressService(view, defsSvc);
        return (defsSvc, progressSvc, view, active);
    }

    [Fact]
    public void Definition_survives_file_roundtrip_and_start_persists_progress()
    {
        var (defs, progress, view, active) = BuildServices();
        active.SetActiveCharacter("Arthur", "Kwatoxi");

        defs.Add(new GandalfTimerDef { Name = "Chest", Duration = TimeSpan.FromHours(1), Region = "Serbule", Map = "Serbule" });
        var id = defs.Definitions[0].Id;
        progress.Start(id);
        defs.Dispose(); // flush definitions
        progress.Dispose();
        view.Dispose();

        var (defs2, progress2, view2, active2) = BuildServices();
        active2.SetActiveCharacter("Arthur", "Kwatoxi");

        defs2.Definitions.Should().HaveCount(1);
        defs2.Definitions[0].Name.Should().Be("Chest");
        var p = progress2.GetProgress(id);
        p.Should().NotBeNull();
        p!.StartedAt.Should().NotBeNull();
        defs2.Dispose();
        progress2.Dispose();
        view2.Dispose();
    }

    [Fact]
    public void Definitions_are_shared_across_characters_progress_is_not()
    {
        var (defs, progress, view, active) = BuildServices();

        active.SetActiveCharacter("Arthur", "Kwatoxi");
        defs.Add(new GandalfTimerDef { Name = "Chest", Duration = TimeSpan.FromHours(1), Region = "Serbule", Map = "Serbule" });
        var id = defs.Definitions[0].Id;
        progress.Start(id);
        progress.GetProgress(id).Should().NotBeNull();
        progress.GetProgress(id)!.StartedAt.Should().NotBeNull();

        active.SetActiveCharacter("Bilbo", "Kwatoxi");

        defs.Definitions.Should().HaveCount(1, "definitions are global");
        var bilboProgress = progress.GetProgress(id);
        (bilboProgress is null || bilboProgress.StartedAt is null)
            .Should().BeTrue("Bilbo has no started progress for this timer");

        defs.Dispose();
        progress.Dispose();
        view.Dispose();
    }

    [Fact]
    public void ProgressChanged_fires_on_character_switch()
    {
        var (defs, progress, view, active) = BuildServices();
        active.SetActiveCharacter("Arthur", "Kwatoxi");

        var fired = 0;
        progress.ProgressChanged += (_, _) => fired++;

        active.SetActiveCharacter("Bilbo", "Kwatoxi");
        fired.Should().BeGreaterThan(0);

        defs.Dispose();
        progress.Dispose();
        view.Dispose();
    }

    [Fact]
    public void Remove_definition_is_invisible_immediately_and_GCs_orphan_on_next_progress_write()
    {
        var (defs, progress, view, active) = BuildServices();
        active.SetActiveCharacter("Arthur", "Kwatoxi");

        defs.Add(new GandalfTimerDef { Name = "Gone", Duration = TimeSpan.FromHours(1) });
        var id = defs.Definitions[0].Id;
        progress.Start(id);
        progress.GetProgress(id).Should().NotBeNull();

        defs.Remove(id);
        defs.Definitions.Should().BeEmpty();
        // Progress for the removed id is still present in-memory until the next flush.
        progress.GetProgress(id).Should().NotBeNull();

        // Trigger a progress write by creating and resetting a second timer.
        defs.Add(new GandalfTimerDef { Name = "Keeper", Duration = TimeSpan.FromHours(1) });
        var keeperId = defs.Definitions[0].Id;
        progress.Start(keeperId);
        progress.Dispose(); // forces a flush which strips orphans
        view.Dispose();

        // New session: orphan should be gone.
        var (defs2, progress2, view2, active2) = BuildServices();
        active2.SetActiveCharacter("Arthur", "Kwatoxi");
        progress2.GetProgress(id).Should().BeNull("orphan progress was stripped on flush");
        progress2.GetProgress(keeperId).Should().NotBeNull();
        defs2.Dispose();
        progress2.Dispose();
        view2.Dispose();
    }

    [Fact]
    public void CheckExpirations_fires_TimerExpired_once_and_stamps_CompletedAt()
    {
        var (defs, progress, view, active) = BuildServices();
        active.SetActiveCharacter("Arthur", "Kwatoxi");

        defs.Add(new GandalfTimerDef { Name = "Short", Duration = TimeSpan.FromSeconds(1) });
        var id = defs.Definitions[0].Id;
        // Seed a progress row that's already past due.
        view.Current!.ByTimerId[id] = new TimerProgress { StartedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5) };

        TimerExpiredEventArgs? captured = null;
        var fireCount = 0;
        progress.TimerExpired += (_, e) => { captured = e; fireCount++; };

        progress.CheckExpirations();
        progress.CheckExpirations();
        progress.CheckExpirations();

        fireCount.Should().Be(1, "expiration fires once per dismissal cycle");
        captured.Should().NotBeNull();
        captured!.Def.Id.Should().Be(id);
        view.Current!.ByTimerId[id].CompletedAt.Should().NotBeNull();

        defs.Dispose();
        progress.Dispose();
        view.Dispose();
    }
}
