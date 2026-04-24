using System.IO;
using FluentAssertions;
using Gorgon.Shared.Character;
using Saruman.Domain;
using Saruman.Services;
using Saruman.Settings;
using Xunit;

namespace Saruman.Tests.Services;

public sealed class SarumanCodebookServiceTests : IDisposable
{
    private readonly string _root;
    private readonly FakeActiveCharacterService _active;

    public SarumanCodebookServiceTests()
    {
        _root = Gorgon.TestSupport.TestPaths.CreateTempDir("saruman");
        _active = new FakeActiveCharacterService();
        _active.SetActiveCharacter("Arthur", "Kwatoxi");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private (SarumanCodebookService svc, PerCharacterView<SarumanState> view, PerCharacterStore<SarumanState> store) NewService()
    {
        var store = new PerCharacterStore<SarumanState>(_root, "saruman.json",
            SarumanJsonContext.Default.SarumanState);
        var view = new PerCharacterView<SarumanState>(_active, store);
        return (new SarumanCodebookService(view), view, store);
    }

    [Fact]
    public void Records_new_discovery()
    {
        var (svc, _, _) = NewService();
        svc.RecordDiscovery(new WordOfPowerDiscovered(DateTime.UtcNow, "FEAVEG", "Fast Swimmer", "swim faster"));

        svc.IsTracked("FEAVEG").Should().BeTrue();
        var w = svc.TryGet("FEAVEG")!;
        w.EffectName.Should().Be("Fast Swimmer");
        w.State.Should().Be(WordOfPowerState.Known);
        w.DiscoveryCount.Should().Be(1);
    }

    [Fact]
    public void Rediscovery_bumps_count_and_restores_spent_to_known()
    {
        var (svc, _, _) = NewService();
        svc.RecordDiscovery(new WordOfPowerDiscovered(DateTime.UtcNow, "FEAVEG", "Fast Swimmer", "swim faster"));
        svc.MarkSpent("FEAVEG", DateTime.UtcNow);
        svc.TryGet("FEAVEG")!.State.Should().Be(WordOfPowerState.Spent);

        svc.RecordDiscovery(new WordOfPowerDiscovered(DateTime.UtcNow, "FEAVEG", "Fast Swimmer", "swim faster"));

        var w = svc.TryGet("FEAVEG")!;
        w.DiscoveryCount.Should().Be(2);
        w.State.Should().Be(WordOfPowerState.Known);
        w.SpentAt.Should().BeNull();
    }

    [Fact]
    public void MarkSpent_is_idempotent()
    {
        var (svc, _, _) = NewService();
        svc.RecordDiscovery(new WordOfPowerDiscovered(DateTime.UtcNow, "FEAVEG", "Fast Swimmer", "swim faster"));
        svc.MarkSpent("FEAVEG", DateTime.UtcNow).Should().BeTrue();
        svc.MarkSpent("FEAVEG", DateTime.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void MarkSpent_returns_false_for_unknown_code()
    {
        var (svc, _, _) = NewService();
        svc.MarkSpent("NOPE", DateTime.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void Persists_across_reload()
    {
        var (svc1, view1, store) = NewService();
        svc1.RecordDiscovery(new WordOfPowerDiscovered(DateTime.UtcNow, "FEAVEG", "Fast Swimmer", "swim faster"));
        svc1.MarkSpent("FEAVEG", DateTime.UtcNow);
        view1.Dispose();

        var view2 = new PerCharacterView<SarumanState>(_active, store);
        var svc2 = new SarumanCodebookService(view2);

        svc2.IsTracked("FEAVEG").Should().BeTrue();
        svc2.TryGet("FEAVEG")!.State.Should().Be(WordOfPowerState.Spent);

        view2.Dispose();
    }

    [Fact]
    public void Raises_CodebookChanged_on_mutations()
    {
        var (svc, _, _) = NewService();
        var fires = 0;
        svc.CodebookChanged += (_, _) => fires++;

        svc.RecordDiscovery(new WordOfPowerDiscovered(DateTime.UtcNow, "FEAVEG", "Fast Swimmer", "swim faster"));
        svc.MarkSpent("FEAVEG", DateTime.UtcNow);
        svc.MarkKnown("FEAVEG");
        svc.Remove("FEAVEG");

        fires.Should().Be(4);
    }

    [Fact]
    public void Character_switch_fires_CodebookChanged_and_swaps_data()
    {
        var (svc, _, _) = NewService();
        svc.RecordDiscovery(new WordOfPowerDiscovered(DateTime.UtcNow, "FEAVEG", "Fast Swimmer", "swim faster"));
        svc.Words.Should().HaveCount(1);

        var fires = 0;
        svc.CodebookChanged += (_, _) => fires++;

        _active.SetActiveCharacter("Bilbo", "Kwatoxi");

        fires.Should().Be(1);
        svc.Words.Should().BeEmpty("Bilbo has never discovered any words");
    }

    [Fact]
    public void Mutations_noop_when_no_character_active()
    {
        _active.Clear();
        var (svc, _, _) = NewService();

        svc.RecordDiscovery(new WordOfPowerDiscovered(DateTime.UtcNow, "FEAVEG", "Fast Swimmer", "swim faster"));

        svc.Words.Should().BeEmpty();
        svc.IsTracked("FEAVEG").Should().BeFalse();
    }
}
