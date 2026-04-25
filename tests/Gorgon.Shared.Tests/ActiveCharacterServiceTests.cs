using System.IO;
using FluentAssertions;
using Gorgon.Shared.Character;
using Gorgon.Shared.Game;
using Xunit;

namespace Gorgon.Shared.Tests;

[Trait("Category", "FileIO")]
public sealed class ActiveCharacterServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly GameConfig _gameConfig;
    private readonly FakePersistence _persistence;

    public ActiveCharacterServiceTests()
    {
        _dir = Gorgon.TestSupport.TestPaths.CreateTempDir("gorgon-active");
        _gameConfig = new GameConfig { GameRoot = Path.GetDirectoryName(_dir)! };
        Directory.CreateDirectory(_gameConfig.ReportsDirectory);
        _persistence = new FakePersistence();
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true); } catch { }
    }

    [Fact]
    public void EmptyReports_ConstructionSucceeds_WithNullActive()
    {
        using var svc = new ActiveCharacterService(_gameConfig, _persistence);
        svc.ActiveCharacterName.Should().BeNull();
        svc.ActiveCharacter.Should().BeNull();
        svc.Characters.Should().BeEmpty();
        svc.StorageReports.Should().BeEmpty();
    }

    [Fact]
    public void PersistedName_IsHonoredAtConstruction()
    {
        _persistence.ActiveCharacterName = "Emraell";
        _persistence.ActiveServer = "Alpha";

        using var svc = new ActiveCharacterService(_gameConfig, _persistence);

        svc.ActiveCharacterName.Should().Be("Emraell");
        svc.ActiveServer.Should().Be("Alpha");
    }

    [Fact]
    public void SetActiveCharacter_FiresEvent_AndPersists()
    {
        using var svc = new ActiveCharacterService(_gameConfig, _persistence);
        var fired = 0;
        svc.ActiveCharacterChanged += (_, _) => fired++;

        svc.SetActiveCharacter("Hits", "Beta");

        fired.Should().Be(1);
        svc.ActiveCharacterName.Should().Be("Hits");
        svc.ActiveServer.Should().Be("Beta");
        _persistence.ActiveCharacterName.Should().Be("Hits");
        _persistence.ActiveServer.Should().Be("Beta");
    }

    [Fact]
    public void SetActiveCharacter_NoOp_DoesNotFire()
    {
        using var svc = new ActiveCharacterService(_gameConfig, _persistence);
        svc.SetActiveCharacter("Hits", "Alpha");
        var fired = 0;
        svc.ActiveCharacterChanged += (_, _) => fired++;

        svc.SetActiveCharacter("Hits", "Alpha");
        svc.SetActiveCharacter("HITS", "alpha"); // case-insensitive match

        fired.Should().Be(0);
    }

    [Fact]
    public void StorageReports_AreDiscovered_WithCorrectShape()
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        File.WriteAllText(Path.Combine(_gameConfig.ReportsDirectory, $"Emraell_Alpha_items_{stamp}.json"), "{}");

        using var svc = new ActiveCharacterService(_gameConfig, _persistence);

        svc.StorageReports.Should().HaveCount(1);
        svc.StorageReports[0].Character.Should().Be("Emraell");
        svc.StorageReports[0].Server.Should().Be("Alpha");
    }

    private sealed class FakePersistence : IActiveCharacterPersistence
    {
        public string? ActiveCharacterName { get; set; }
        public string? ActiveServer { get; set; }
    }
}
