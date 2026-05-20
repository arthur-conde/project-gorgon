using System.Collections.Generic;

namespace Mithril.Tools.LogSanitizer;

public sealed class NameRegistry
{
    private readonly Dictionary<string, string> _mappings = new();
    private int _nextPlayerIndex = 1;
    private string? _ownCharacter;

    public IReadOnlyDictionary<string, string> AllMappings => _mappings;

    public void RegisterOwnCharacter(string name)
    {
        _ownCharacter = name;
        _mappings[name] = "<CHARACTER>";
    }

    public void RegisterOtherPlayer(string name)
    {
        if (name == _ownCharacter)
            return;
        if (_mappings.ContainsKey(name))
            return;
        _mappings[name] = $"<PLAYER_{_nextPlayerIndex++}>";
    }

    public string? TokenFor(string name)
    {
        return _mappings.TryGetValue(name, out var token) ? token : null;
    }
}
