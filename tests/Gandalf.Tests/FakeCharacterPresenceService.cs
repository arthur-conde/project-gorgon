using Mithril.Shared.Character;

namespace Gandalf.Tests;

internal sealed class FakeCharacterPresenceService : ICharacterPresenceService
{
    private readonly Dictionary<(string, string), DateTimeOffset?> _map = new();

    public void Set(string character, string server, DateTimeOffset? lastActiveAt)
    {
        _map[(character, server)] = lastActiveAt;
    }

    public DateTimeOffset? GetLastActiveAt(string character, string server)
        => _map.TryGetValue((character, server), out var value) ? value : null;
}
