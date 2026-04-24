using System.IO;
using FluentAssertions;
using Gorgon.Shared.Character;
using Gorgon.Shared.Storage;
using Xunit;

namespace Gorgon.Shared.Tests.Character;

public sealed class PerCharacterLegacyFanoutTests : IDisposable
{
    private readonly string _root;
    private readonly FakeActiveCharacterService _active;

    public PerCharacterLegacyFanoutTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"gorgon-fanout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _active = new FakeActiveCharacterService
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
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void FanOut_writes_per_character_files_and_returns_empty_when_all_resolved()
    {
        var store = new PerCharacterStore<TestState>(_root, "test.json", TestStateJsonContext.Default.TestState);

        var unresolved = PerCharacterLegacyFanout.FanOut(
            names: ["Arthur", "Bilbo"],
            store: store,
            active: _active,
            extractFor: name => new TestState { Value = $"value-for-{name}" });

        unresolved.Should().BeEmpty();
        store.Load("Arthur", "Kwatoxi").Value.Should().Be("value-for-Arthur");
        store.Load("Bilbo", "Kwatoxi").Value.Should().Be("value-for-Bilbo");
    }

    [Fact]
    public void FanOut_returns_unresolved_names_and_skips_their_writes()
    {
        var store = new PerCharacterStore<TestState>(_root, "test.json", TestStateJsonContext.Default.TestState);

        var unresolved = PerCharacterLegacyFanout.FanOut(
            names: ["Arthur", "NoExportChar"],
            store: store,
            active: _active,
            extractFor: name => new TestState { Value = $"value-for-{name}" });

        unresolved.Should().ContainSingle().Which.Should().Be("NoExportChar");
        store.Load("Arthur", "Kwatoxi").Value.Should().Be("value-for-Arthur");
    }

    [Fact]
    public void FanOut_skips_names_whose_target_file_already_exists()
    {
        var store = new PerCharacterStore<TestState>(_root, "test.json", TestStateJsonContext.Default.TestState);
        store.Save("Arthur", "Kwatoxi", new TestState { Value = "keep-me" });

        var unresolved = PerCharacterLegacyFanout.FanOut(
            names: ["Arthur"],
            store: store,
            active: _active,
            extractFor: name => new TestState { Value = "would-have-written" });

        unresolved.Should().BeEmpty();
        store.Load("Arthur", "Kwatoxi").Value.Should().Be("keep-me",
            "existing per-char files are never clobbered by the fanout");
    }

    [Fact]
    public void FanOut_invalidates_view_cache_when_view_supplied_and_any_write_happened()
    {
        var store = new PerCharacterStore<TestState>(_root, "test.json", TestStateJsonContext.Default.TestState);
        _active.SetActiveCharacter("Arthur", "Kwatoxi");
        using var view = new PerCharacterView<TestState>(_active, store);

        // Prime the view's cache with an empty state (simulating the DI-time Rebuild path).
        _ = view.Current;

        var currentChangedFired = 0;
        view.CurrentChanged += (_, _) => currentChangedFired++;

        PerCharacterLegacyFanout.FanOut(
            names: ["Arthur", "Bilbo"],
            store: store,
            active: _active,
            extractFor: name => new TestState { Value = $"value-for-{name}" },
            view: view);

        currentChangedFired.Should().Be(1, "view should be invalidated exactly once after a successful fanout");
        view.Current!.Value.Should().Be("value-for-Arthur",
            "next read should reload from disk, not return the stale cached empty state");
    }

    [Fact]
    public void FanOut_does_not_invalidate_when_nothing_was_written()
    {
        var store = new PerCharacterStore<TestState>(_root, "test.json", TestStateJsonContext.Default.TestState);
        _active.SetActiveCharacter("Arthur", "Kwatoxi");
        using var view = new PerCharacterView<TestState>(_active, store);
        _ = view.Current;

        var currentChangedFired = 0;
        view.CurrentChanged += (_, _) => currentChangedFired++;

        // Only unresolved names — nothing writes.
        PerCharacterLegacyFanout.FanOut(
            names: ["NoExportChar"],
            store: store,
            active: _active,
            extractFor: name => new TestState { Value = "unused" },
            view: view);

        currentChangedFired.Should().Be(0, "no write → no invalidate → no event");
    }
}
