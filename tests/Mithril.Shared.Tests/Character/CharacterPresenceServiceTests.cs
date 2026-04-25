using System.IO;
using FluentAssertions;
using Mithril.Shared.Character;
using Xunit;

namespace Mithril.Shared.Tests.Character;

[Trait("Category", "FileIO")]
public sealed class CharacterPresenceServiceTests : IDisposable
{
    private readonly string _root;
    private readonly FakeActiveCharacterService _active;
    private readonly PerCharacterStore<CharacterPresence> _store;
    private readonly CharacterPresenceService _svc;

    public CharacterPresenceServiceTests()
    {
        _root = Mithril.TestSupport.TestPaths.CreateTempDir("mithril-presence");
        _active = new FakeActiveCharacterService();
        _store = new PerCharacterStore<CharacterPresence>(_root, "character.json",
            CharacterPresenceJsonContext.Default.CharacterPresence);
        _svc = new CharacterPresenceService(_active, _store);
    }

    public void Dispose()
    {
        _svc.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task OnSwitch_stamps_outgoing_character_not_incoming()
    {
        _active.SetActiveCharacter("Arthur", "Kwatoxi");
        await _svc.StartAsync(CancellationToken.None);

        var before = DateTimeOffset.UtcNow;
        _active.SetActiveCharacter("Bilbo", "Kwatoxi");
        var after = DateTimeOffset.UtcNow;

        _svc.GetLastActiveAt("Arthur", "Kwatoxi")
            .Should().NotBeNull()
            .And.Subject.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);

        _svc.GetLastActiveAt("Bilbo", "Kwatoxi")
            .Should().BeNull("incoming character is not stamped until it in turn becomes outgoing");
    }

    [Fact]
    public async Task FirstCharacter_with_no_prior_tracked_writes_nothing_on_switch_in()
    {
        // Service starts with no character tracked.
        await _svc.StartAsync(CancellationToken.None);

        _active.SetActiveCharacter("Arthur", "Kwatoxi");

        // No outgoing → no write yet.
        _svc.GetLastActiveAt("Arthur", "Kwatoxi").Should().BeNull();

        // Switching away from Arthur now writes.
        _active.SetActiveCharacter("Bilbo", "Kwatoxi");
        _svc.GetLastActiveAt("Arthur", "Kwatoxi").Should().NotBeNull();
    }

    [Fact]
    public async Task StopAsync_stamps_currently_active_character()
    {
        _active.SetActiveCharacter("Arthur", "Kwatoxi");
        await _svc.StartAsync(CancellationToken.None);

        _svc.GetLastActiveAt("Arthur", "Kwatoxi").Should().BeNull();

        await _svc.StopAsync(CancellationToken.None);

        _svc.GetLastActiveAt("Arthur", "Kwatoxi").Should().NotBeNull();
    }

    [Fact]
    public async Task GetLastActiveAt_returns_null_when_file_missing()
    {
        await _svc.StartAsync(CancellationToken.None);

        _svc.GetLastActiveAt("Nobody", "Kwatoxi").Should().BeNull();
    }
}
