using System.IO;
using FluentAssertions;
using Mithril.Shared.Character;
using Saruman.Services;
using Saruman.Settings;
using Xunit;

namespace Saruman.Tests.Services;

/// <summary>
/// Tests for the module-internal user-override ledger (#603 — post-split).
/// One-way Sticky Spent: the user can mark a code Spent manually for
/// offline-burn cases; <see cref="SarumanOverrideService.ClearOverride"/>
/// removes the manual mark only (the underlying view's Spent stays
/// monotonic).
/// </summary>
[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class SarumanOverrideServiceTests : IDisposable
{
    private readonly string _root;
    private readonly FakeActiveCharacterService _active;

    public SarumanOverrideServiceTests()
    {
        _root = Mithril.TestSupport.TestPaths.CreateTempDir("saruman-override");
        _active = new FakeActiveCharacterService();
        _active.SetActiveCharacter("Arthur", "Kwatoxi");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private (SarumanOverrideService svc, PerCharacterView<SarumanState> view) Build()
    {
        var store = new PerCharacterStore<SarumanState>(_root, "saruman.json",
            SarumanJsonContext.Default.SarumanState);
        var view = new PerCharacterView<SarumanState>(_active, store);
        return (new SarumanOverrideService(view), view);
    }

    [Fact]
    public void MarkSpent_records_override_and_persists()
    {
        var (svc, view) = Build();
        svc.MarkSpent("FEAVEG").Should().BeTrue();
        svc.IsSpent("FEAVEG").Should().BeTrue();
        view.Dispose();

        // Reload — override survives.
        var (svc2, view2) = Build();
        svc2.IsSpent("FEAVEG").Should().BeTrue();
        view2.Dispose();
    }

    [Fact]
    public void MarkSpent_is_idempotent_returning_false_when_already_marked()
    {
        var (svc, view) = Build();
        svc.MarkSpent("FEAVEG").Should().BeTrue();
        svc.MarkSpent("FEAVEG").Should().BeFalse();
        view.Dispose();
    }

    [Fact]
    public void ClearOverride_removes_the_manual_mark()
    {
        var (svc, view) = Build();
        svc.MarkSpent("FEAVEG");
        svc.ClearOverride("FEAVEG").Should().BeTrue();
        svc.IsSpent("FEAVEG").Should().BeFalse();
        svc.ClearOverride("FEAVEG").Should().BeFalse();
        view.Dispose();
    }

    [Fact]
    public void OverridesChanged_fires_on_mark_clear_and_character_switch()
    {
        var (svc, view) = Build();
        var fires = 0;
        svc.OverridesChanged += (_, _) => fires++;

        svc.MarkSpent("FEAVEG");
        svc.ClearOverride("FEAVEG");
        _active.SetActiveCharacter("Bilbo", "Kwatoxi");

        fires.Should().Be(3);
        view.Dispose();
    }

    [Fact]
    public void Mutations_noop_when_no_character_active()
    {
        var (svc, view) = Build();
        _active.SetActiveCharacter(string.Empty, string.Empty);

        svc.MarkSpent("FEAVEG").Should().BeFalse();
        svc.IsSpent("FEAVEG").Should().BeFalse();
        view.Dispose();
    }

    [Fact]
    public void DismissMigrationHint_clears_flag_persists_and_fires_event()
    {
        // Seed a v1 saruman.json on disk so the store's Load runs Migrate()
        // and stamps ShowPreSplitMigrationHint, the production path the UX
        // banner sees.
        var charDir = Path.Combine(_root, "Arthur_Kwatoxi");
        Directory.CreateDirectory(charDir);
        File.WriteAllText(Path.Combine(charDir, "saruman.json"),
            """{"schemaVersion":1,"spentOverrides":[]}""");

        var (svc, view) = Build();
        var fires = 0;
        svc.OverridesChanged += (_, _) => fires++;

        svc.ShowMigrationHint.Should().BeTrue("Migrate() set the flag on load");
        svc.DismissMigrationHint().Should().BeTrue();
        svc.ShowMigrationHint.Should().BeFalse();
        fires.Should().Be(1, "dismiss piggybacks on OverridesChanged for VM refresh");

        // Idempotent: a second dismiss is a no-op.
        svc.DismissMigrationHint().Should().BeFalse();
        view.Dispose();

        // Round-trip: the dismissal persisted to disk; reloading does not
        // re-trigger the hint (Migrate doesn't run for current-version files).
        var (svc2, view2) = Build();
        svc2.ShowMigrationHint.Should().BeFalse();
        view2.Dispose();
    }
}
