using System.IO;
using FluentAssertions;
using Gorgon.Shared.Settings;
using Saruman.Domain;
using Saruman.Services;
using Saruman.Settings;
using Xunit;

namespace Saruman.Tests.Services;

public sealed class SarumanCodebookServiceTests
{
    private static SarumanCodebookService NewService()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"saruman-{Guid.NewGuid():N}.json");
        var store = new JsonSettingsStore<SarumanState>(tmp, SarumanJsonContext.Default.SarumanState);
        return new SarumanCodebookService(store);
    }

    [Fact]
    public void Records_new_discovery()
    {
        var svc = NewService();
        svc.RecordDiscovery(new WordOfPowerDiscovered(DateTime.UtcNow, "FEAVEG", "Fast Swimmer", "swim faster"));

        svc.IsTracked("FEAVEG").Should().BeTrue();
        var w = svc.TryGet("FEAVEG")!;
        w.EffectName.Should().Be("Fast Swimmer");
        w.Tier.Should().Be(WordOfPowerTier.Tier1);
        w.State.Should().Be(WordOfPowerState.Known);
        w.DiscoveryCount.Should().Be(1);
    }

    [Fact]
    public void Rediscovery_bumps_count_and_restores_spent_to_known()
    {
        var svc = NewService();
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
        var svc = NewService();
        svc.RecordDiscovery(new WordOfPowerDiscovered(DateTime.UtcNow, "FEAVEG", "Fast Swimmer", "swim faster"));
        svc.MarkSpent("FEAVEG", DateTime.UtcNow).Should().BeTrue();
        svc.MarkSpent("FEAVEG", DateTime.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void MarkSpent_returns_false_for_unknown_code()
    {
        var svc = NewService();
        svc.MarkSpent("NOPE", DateTime.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void Persists_across_reload()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"saruman-{Guid.NewGuid():N}.json");
        var store = new JsonSettingsStore<SarumanState>(tmp, SarumanJsonContext.Default.SarumanState);

        var svc1 = new SarumanCodebookService(store);
        svc1.RecordDiscovery(new WordOfPowerDiscovered(DateTime.UtcNow, "FEAVEG", "Fast Swimmer", "swim faster"));
        svc1.MarkSpent("FEAVEG", DateTime.UtcNow);

        var svc2 = new SarumanCodebookService(store);
        svc2.IsTracked("FEAVEG").Should().BeTrue();
        svc2.TryGet("FEAVEG")!.State.Should().Be(WordOfPowerState.Spent);

        File.Delete(tmp);
    }

    [Fact]
    public void Raises_CodebookChanged_on_mutations()
    {
        var svc = NewService();
        var fires = 0;
        svc.CodebookChanged += (_, _) => fires++;

        svc.RecordDiscovery(new WordOfPowerDiscovered(DateTime.UtcNow, "FEAVEG", "Fast Swimmer", "swim faster"));
        svc.MarkSpent("FEAVEG", DateTime.UtcNow);
        svc.MarkKnown("FEAVEG");
        svc.Remove("FEAVEG");

        fires.Should().Be(4);
    }
}
