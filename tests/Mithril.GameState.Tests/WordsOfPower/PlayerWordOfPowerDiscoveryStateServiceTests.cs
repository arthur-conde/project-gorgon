using System.IO;
using FluentAssertions;
using Mithril.GameState.Tests.Quests;
using Mithril.GameState.WordsOfPower;
using Mithril.Shared.Character;
using Mithril.TestSupport;
using Mithril.WorldSim;
using Xunit;

namespace Mithril.GameState.Tests.WordsOfPower;

[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class PlayerWordOfPowerDiscoveryStateServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _charactersDir;

    public PlayerWordOfPowerDiscoveryStateServiceTests()
    {
        _dir = TestPaths.CreateTempDir("wop_discovery_folder");
        _charactersDir = Path.Combine(_dir, "characters");
        Directory.CreateDirectory(_charactersDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static DateTime Ts(int s) => new(2026, 5, 22, 8, 0, s, DateTimeKind.Utc);
    private static Frame<WordOfPowerDiscoveryFrame> F(string code, string effect, string desc, DateTime ts) =>
        new(new DateTimeOffset(ts, TimeSpan.Zero), new WordOfPowerDiscoveryFrame(code, effect, desc));

    private sealed class StubClock : IWorldClock
    {
        public DateTimeOffset Now => DateTimeOffset.UtcNow;
        public long Frame => 0;
        public WorldMode Mode => WorldMode.Live;
    }

    private (PlayerWordOfPowerDiscoveryStateService svc, FakeActiveCharacterService active,
             PerCharacterView<PlayerWordOfPowerDiscoveryStateData> view) Build(
        string character = "Arthur", string server = "Kwatoxi")
    {
        var active = new FakeActiveCharacterService();
        active.SetActiveCharacter(character, server);
        var store = new PerCharacterStore<PlayerWordOfPowerDiscoveryStateData>(
            _charactersDir, "wop-discovery.json",
            PlayerWordOfPowerDiscoveryStateJsonContext.Default.PlayerWordOfPowerDiscoveryStateData);
        var view = new PerCharacterView<PlayerWordOfPowerDiscoveryStateData>(active, store);
        var svc = new PlayerWordOfPowerDiscoveryStateService(view);
        return (svc, active, view);
    }

    [Fact]
    public void First_discovery_emits_PlayerWordOfPowerDiscovered_and_persists()
    {
        var (svc, _, view) = Build();
        var changes = svc.Apply(F("FEAVEG", "Fast Swimmer", "swim faster", Ts(1)), new StubClock());

        changes.Should().ContainSingle();
        var ev = changes[0].Should().BeOfType<PlayerWordOfPowerDiscovered>().Subject;
        ev.Code.Should().Be("FEAVEG");
        ev.EffectName.Should().Be("Fast Swimmer");
        ev.Description.Should().Be("swim faster");
        ev.Timestamp.Should().Be(Ts(1));

        svc.TryGet("FEAVEG").Should().NotBeNull();
        svc.TryGet("FEAVEG")!.EffectName.Should().Be("Fast Swimmer");

        view.Dispose();
    }

    [Fact]
    public void Duplicate_discovery_for_known_code_is_elided()
    {
        var (svc, _, view) = Build();
        svc.Apply(F("FEAVEG", "Fast Swimmer", "swim faster", Ts(1)), new StubClock());

        var changes = svc.Apply(F("FEAVEG", "Fast Swimmer", "swim faster", Ts(2)), new StubClock());

        changes.Should().BeEmpty();
        view.Dispose();
    }

    [Fact]
    public void Drops_frames_when_no_active_character()
    {
        var (svc, active, view) = Build();
        active.SetActiveCharacter("", "");

        // Sanity — clearing both name/server reverts Current to null in the view.
        view.Current.Should().BeNull();

        var changes = svc.Apply(F("FEAVEG", "Fast Swimmer", "swim faster", Ts(1)), new StubClock());

        changes.Should().BeEmpty();
        svc.TryGet("FEAVEG").Should().BeNull();
        view.Dispose();
    }

    [Fact]
    public void Persists_across_view_reload()
    {
        var (svc, _, view) = Build();
        svc.Apply(F("FEAVEG", "Fast Swimmer", "swim faster", Ts(1)), new StubClock());
        view.Dispose();

        var (svc2, _, view2) = Build();
        svc2.TryGet("FEAVEG").Should().NotBeNull();
        svc2.TryGet("FEAVEG")!.EffectName.Should().Be("Fast Swimmer");
        view2.Dispose();
    }
}
