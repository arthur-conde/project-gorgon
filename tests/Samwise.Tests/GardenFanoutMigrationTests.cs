using System.IO;
using System.Text.Json;
using FluentAssertions;
using Gorgon.Shared.Character;
using Gorgon.Shared.Storage;
using Samwise.State;
using Xunit;

namespace Samwise.Tests;

public sealed class GardenFanoutMigrationTests : IDisposable
{
    private readonly string _root;
    private readonly string _samwiseDir;
    private readonly string _charactersRoot;

    public GardenFanoutMigrationTests()
    {
        _root = Gorgon.TestSupport.TestPaths.CreateTempDir("gorgon-samwise-fanout");
        _samwiseDir = Path.Combine(_root, "Samwise");
        _charactersRoot = Path.Combine(_root, "characters");
        Directory.CreateDirectory(_samwiseDir);
        Directory.CreateDirectory(_charactersRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Splits_legacy_PlotsByChar_into_per_character_files()
    {
        var legacyPath = Path.Combine(_samwiseDir, "garden-state.json");
        var legacy = new GardenState
        {
            PlotsByChar =
            {
                ["Arthur"] = new Dictionary<string, PersistedPlot>
                {
                    ["plot1"] = new PersistedPlot { CropType = "Onion", Title = "Onion patch" },
                },
                ["Bilbo"] = new Dictionary<string, PersistedPlot>
                {
                    ["plot2"] = new PersistedPlot { CropType = "Carrot", Title = "Carrot patch" },
                },
            },
        };
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(legacy, GardenStateJsonContext.Default.GardenState));

        var active = new FakeActiveCharacterService
        {
            Characters =
            [
                new CharacterSnapshot("Arthur", "Kwatoxi", default,
                    new Dictionary<string, CharacterSkill>(), new Dictionary<string, int>(),
                    new Dictionary<string, string>()),
                new CharacterSnapshot("Bilbo", "Kwatoxi", default,
                    new Dictionary<string, CharacterSkill>(), new Dictionary<string, int>(),
                    new Dictionary<string, string>()),
            ],
        };
        var store = new PerCharacterStore<GardenCharacterState>(_charactersRoot, "samwise.json",
            GardenCharacterStateJsonContext.Default.GardenCharacterState);

        var mig = new GardenFanoutMigration(_samwiseDir, store, active);
        await mig.StartAsync(CancellationToken.None);

        store.Load("Arthur", "Kwatoxi").Plots.Should().ContainKey("plot1");
        store.Load("Arthur", "Kwatoxi").Plots["plot1"].CropType.Should().Be("Onion");
        store.Load("Bilbo", "Kwatoxi").Plots.Should().ContainKey("plot2");
        store.Load("Bilbo", "Kwatoxi").Plots["plot2"].CropType.Should().Be("Carrot");

        File.Exists(legacyPath).Should().BeFalse("legacy file should be deleted when all chars resolved");
    }

    [Fact]
    public async Task Leaves_unresolved_characters_in_pruned_legacy_file()
    {
        var legacyPath = Path.Combine(_samwiseDir, "garden-state.json");
        var legacy = new GardenState
        {
            PlotsByChar =
            {
                ["Arthur"] = new Dictionary<string, PersistedPlot> { ["plot1"] = new() { CropType = "Onion" } },
                ["Stranger"] = new Dictionary<string, PersistedPlot> { ["plot2"] = new() { CropType = "Pumpkin" } },
            },
        };
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(legacy, GardenStateJsonContext.Default.GardenState));

        var active = new FakeActiveCharacterService
        {
            Characters =
            [
                new CharacterSnapshot("Arthur", "Kwatoxi", default,
                    new Dictionary<string, CharacterSkill>(), new Dictionary<string, int>(),
                    new Dictionary<string, string>()),
            ],
        };
        var store = new PerCharacterStore<GardenCharacterState>(_charactersRoot, "samwise.json",
            GardenCharacterStateJsonContext.Default.GardenCharacterState);

        var mig = new GardenFanoutMigration(_samwiseDir, store, active);
        await mig.StartAsync(CancellationToken.None);

        store.Load("Arthur", "Kwatoxi").Plots.Should().ContainKey("plot1");
        File.Exists(legacyPath).Should().BeTrue("legacy retained for unresolved Stranger");

        var remaining = JsonSerializer.Deserialize(File.ReadAllText(legacyPath), GardenStateJsonContext.Default.GardenState)!;
        remaining.PlotsByChar.Should().ContainKey("Stranger");
        remaining.PlotsByChar.Should().NotContainKey("Arthur", "Arthur was already migrated — removed from legacy");
    }

    [Fact]
    public async Task NoOp_when_legacy_file_missing()
    {
        var active = new FakeActiveCharacterService();
        var store = new PerCharacterStore<GardenCharacterState>(_charactersRoot, "samwise.json",
            GardenCharacterStateJsonContext.Default.GardenCharacterState);

        var mig = new GardenFanoutMigration(_samwiseDir, store, active);
        var act = async () => await mig.StartAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }
}
