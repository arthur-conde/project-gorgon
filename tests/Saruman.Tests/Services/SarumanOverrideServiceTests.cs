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
}
