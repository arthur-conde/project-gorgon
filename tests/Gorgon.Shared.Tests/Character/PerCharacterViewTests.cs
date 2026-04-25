using System.IO;
using FluentAssertions;
using Gorgon.Shared.Character;
using Xunit;

namespace Gorgon.Shared.Tests.Character;

[Trait("Category", "FileIO")]
public sealed class PerCharacterViewTests : IDisposable
{
    private readonly string _root;
    private readonly FakeActiveCharacterService _active;
    private readonly PerCharacterStore<TestState> _store;

    public PerCharacterViewTests()
    {
        _root = Gorgon.TestSupport.TestPaths.CreateTempDir("gorgon-per-char-view");
        _active = new FakeActiveCharacterService();
        _store = new PerCharacterStore<TestState>(_root, "test.json", TestStateJsonContext.Default.TestState);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Current_is_null_until_both_name_and_server_are_set()
    {
        using var view = new PerCharacterView<TestState>(_active, _store);
        view.Current.Should().BeNull();

        _active.SetActiveCharacter("Arthur", "Kwatoxi");
        view.Current.Should().NotBeNull();
    }

    [Fact]
    public void Current_caches_the_loaded_state_between_reads()
    {
        using var view = new PerCharacterView<TestState>(_active, _store);
        _active.SetActiveCharacter("Arthur", "Kwatoxi");

        var first = view.Current!;
        first.Value = "mutated-in-memory";

        var second = view.Current!;
        second.Should().BeSameAs(first, "second read should hit the cache, not reload");
        second.Value.Should().Be("mutated-in-memory");
    }

    [Fact]
    public void Character_switch_saves_outgoing_clears_cache_and_fires_CurrentChanged()
    {
        using var view = new PerCharacterView<TestState>(_active, _store);

        _active.SetActiveCharacter("Arthur", "Kwatoxi");
        view.Current!.Value = "arthur-work";

        var fired = 0;
        view.CurrentChanged += (_, _) => fired++;

        _active.SetActiveCharacter("Bilbo", "Kwatoxi");
        fired.Should().Be(1);

        // Arthur's data was flushed on switch
        var arthurPath = _store.GetFilePath("Arthur", "Kwatoxi");
        File.Exists(arthurPath).Should().BeTrue();
        _store.Load("Arthur", "Kwatoxi").Value.Should().Be("arthur-work");

        // Bilbo's Current is a fresh new state
        view.Current!.Value.Should().Be("");
    }

    [Fact]
    public void Save_persists_current_state_immediately()
    {
        using var view = new PerCharacterView<TestState>(_active, _store);
        _active.SetActiveCharacter("Arthur", "Kwatoxi");
        view.Current!.Value = "saved";

        view.Save();

        _store.Load("Arthur", "Kwatoxi").Value.Should().Be("saved");
    }

    [Fact]
    public void Save_is_a_noop_when_no_character_is_active()
    {
        using var view = new PerCharacterView<TestState>(_active, _store);
        var act = () => view.Save();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_flushes_cached_state_for_active_character()
    {
        _active.SetActiveCharacter("Arthur", "Kwatoxi");
        var view = new PerCharacterView<TestState>(_active, _store);
        view.Current!.Value = "flush-on-dispose";

        view.Dispose();

        _store.Load("Arthur", "Kwatoxi").Value.Should().Be("flush-on-dispose");
    }
}
