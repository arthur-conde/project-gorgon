using Arda.Composition;
using Elrond.Services;
using FluentAssertions;
using Mithril.GameReports;
using Mithril.Shared.Character;
using Xunit;

namespace Elrond.Tests;

public class LiveProgressionAdapterTests
{
    private static readonly CharacterSnapshot Export = new(
        "Galadriel", "Eltibule", DateTimeOffset.Parse("2026-05-01T12:00:00Z"),
        new Dictionary<string, CharacterSkill>
        {
            ["Cooking"] = new(10, 0, 50, 100),
            ["Smithing"] = new(5, 0, 0, 100),
        },
        new Dictionary<string, int> { ["Bread"] = 3 },
        new Dictionary<string, string> { ["Hulon"] = "Friends" });

    [Fact]
    public void ExportOnly_LiveEmpty_ReturnsExport()
    {
        var (adapter, _) = Fixture(Export, live: null);
        var snap = adapter.GetMergedSnapshot();

        adapter.LastDataSource.Should().Be(ProgressionDataSource.ExportOnly);
        snap.Should().BeEquivalentTo(Export);
        snap!.NpcFavor.Should().ContainKey("Hulon");
    }

    [Fact]
    public void LiveOnly_NoExport_SynthesizesSnapshot()
    {
        var live = new FakePlayerProgressionState();
        live.SetSkill("Cooking", 12, currentXp: 10, xpNeeded: 90);
        live.SetRecipe("Bread", 2);

        var (adapter, _) = Fixture(null, live, name: "Galadriel", server: "Eltibule");

        var snap = adapter.GetMergedSnapshot();

        adapter.LastDataSource.Should().Be(ProgressionDataSource.LiveOnly);
        snap.Should().NotBeNull();
        snap!.Name.Should().Be("Galadriel");
        snap.Server.Should().Be("Eltibule");
        snap.Skills["Cooking"].Level.Should().Be(12);
        snap.Skills["Cooking"].XpTowardNextLevel.Should().Be(10);
        snap.RecipeCompletions["Bread"].Should().Be(2);
        snap.NpcFavor.Should().BeEmpty();
    }

    [Fact]
    public void Merged_LiveWinsOnOverlap()
    {
        var live = new FakePlayerProgressionState();
        live.SetSkill("Cooking", 15, currentXp: 20, xpNeeded: 80);

        var (adapter, _) = Fixture(Export, live);
        var snap = adapter.GetMergedSnapshot();

        adapter.LastDataSource.Should().Be(ProgressionDataSource.Merged);
        snap!.Skills["Cooking"].Level.Should().Be(15);
        snap.Skills["Cooking"].XpTowardNextLevel.Should().Be(20);
        snap.Skills["Smithing"].Level.Should().Be(5);
        snap.NpcFavor["Hulon"].Should().Be("Friends");
    }

    [Fact]
    public void Merged_UnionDisjointKeys()
    {
        var live = new FakePlayerProgressionState();
        live.SetSkill("Gardening", 3);
        live.SetRecipe("TomatoSeed", 1);

        var (adapter, _) = Fixture(Export, live);
        var snap = adapter.GetMergedSnapshot();

        snap!.Skills.Should().ContainKeys("Cooking", "Smithing", "Gardening");
        snap.RecipeCompletions.Should().ContainKeys("Bread", "TomatoSeed");
        snap.RecipeCompletions["TomatoSeed"].Should().Be(1);
    }

    [Fact]
    public void LiveStateChanged_RaisesChanged()
    {
        var live = new FakePlayerProgressionState();
        var (adapter, _) = Fixture(Export, live);

        var count = 0;
        adapter.Changed += () => count++;

        live.SetSkill("Cooking", 11);

        count.Should().Be(1);
    }

    [Fact]
    public void ExportChanged_RaisesChanged()
    {
        var reports = new MutableFakeGameReports(Export);
        var live = new FakePlayerProgressionState();
        var active = new FakeActiveChar(Export);
        var adapter = new LiveProgressionAdapter(live, reports, active, new FakeSessionComposer(
            new ComposedSession(Export.Name, Export.Server, Export.ExportedAt, TimeSpan.Zero, "test")));

        var count = 0;
        adapter.Changed += () => count++;

        reports.RaiseSnapshotsChanged();

        count.Should().Be(1);
    }

    [Fact]
    public void NoData_ReturnsNull()
    {
        var (adapter, _) = Fixture(null, new FakePlayerProgressionState());
        adapter.GetMergedSnapshot().Should().BeNull();
        adapter.LastDataSource.Should().Be(ProgressionDataSource.None);
    }

    private static (LiveProgressionAdapter adapter, FakePlayerProgressionState live) Fixture(
        CharacterSnapshot? export,
        FakePlayerProgressionState? live,
        string name = "Galadriel",
        string server = "Eltibule",
        ComposedSession? session = null)
    {
        live ??= new FakePlayerProgressionState();
        var reports = new FakeGameReports(export);
        IActiveCharacterService active = export is not null
            ? new FakeActiveChar(export)
            : new NamedFakeActiveChar(name, server);
        session ??= new ComposedSession(name, server, DateTimeOffset.UtcNow, TimeSpan.Zero, $"{name}:test");
        var sessionComposer = new FakeSessionComposer(session);
        return (new LiveProgressionAdapter(live, reports, active, sessionComposer), live);
    }

    [Fact]
    public void LiveIgnored_WhenActiveCharacterDiffersFromSession()
    {
        var live = new FakePlayerProgressionState();
        live.SetSkill("Cooking", 99);

        var export = Export;
        var active = new FakeActiveChar(export);
        var session = new FakeSessionComposer(new ComposedSession("OtherChar", "Eltibule", DateTimeOffset.UtcNow, TimeSpan.Zero, "x"));
        var adapter = new LiveProgressionAdapter(live, new FakeGameReports(export), active, session);

        var snap = adapter.GetMergedSnapshot();

        adapter.LastDataSource.Should().Be(ProgressionDataSource.ExportOnly);
        snap!.Skills["Cooking"].Level.Should().Be(10);
    }

    private sealed class NamedFakeActiveChar(string name, string server) : IActiveCharacterService
    {
        public IReadOnlyList<CharacterSnapshot> Characters => [];
        public IReadOnlyList<ReportFileInfo> StorageReports { get; } = [];
        public string? ActiveCharacterName => name;
        public string? ActiveServer => server;
        public CharacterSnapshot? ActiveCharacter => null;
        public ReportFileInfo? ActiveStorageReport => null;
        public StorageReport? ActiveStorageContents => null;
        public void SetActiveCharacter(string n, string s) { }
        public void Refresh() { }
#pragma warning disable CS0067
        public event EventHandler? ActiveCharacterChanged;
#pragma warning restore CS0067
        public event EventHandler? CharacterExportsChanged { add { } remove { } }
        public event EventHandler? StorageReportsChanged { add { } remove { } }
        public void Dispose() { }
    }

    private sealed class MutableFakeGameReports : IGameReportsService
    {
        private CharacterSnapshot? _active;
        public MutableFakeGameReports(CharacterSnapshot active) => _active = active;

        public IReadOnlyList<ReportFileInfo> StorageReports => [];
        public IReadOnlyList<CharacterSnapshot> CharacterSnapshots => _active is null ? [] : [_active];
        public ReportFileInfo? GetStorageReport(string? character, string? server) => null;
        public StorageReport? GetStorageContents(string? character, string? server) => null;

        public CharacterSnapshot? GetCharacterSnapshot(string? character, string? server)
        {
            if (_active is null || string.IsNullOrEmpty(character)) return null;
            if (!_active.Name.Equals(character, StringComparison.OrdinalIgnoreCase)) return null;
            if (!string.IsNullOrEmpty(server)
                && !_active.Server.Equals(server, StringComparison.OrdinalIgnoreCase)) return null;
            return _active;
        }

        public void RaiseSnapshotsChanged() => CharacterSnapshotsChanged?.Invoke(this, EventArgs.Empty);

        public event EventHandler? StorageReportsChanged { add { } remove { } }
        public event EventHandler? CharacterSnapshotsChanged;
        public void Refresh() { }
        public void Dispose() { }
    }
}
