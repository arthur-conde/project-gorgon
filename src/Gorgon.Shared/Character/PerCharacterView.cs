namespace Gorgon.Shared.Character;

/// <summary>
/// Typed wrapper over a <c>Dictionary&lt;string, T&gt;</c> that auto-tracks the active
/// character and exposes <see cref="Current"/> as the entry for the current character,
/// creating it lazily on first access. Fires <see cref="CurrentChanged"/> on switch.
///
/// Modules still own the dictionary (it is the persisted form); the view provides
/// a convenient handle for reading/writing per-character state and a single event
/// to subscribe to instead of <see cref="IActiveCharacterService.ActiveCharacterChanged"/>.
/// </summary>
public sealed class PerCharacterView<T> where T : class, new()
{
    private readonly IActiveCharacterService _active;
    private readonly Dictionary<string, T> _store;

    public PerCharacterView(IActiveCharacterService active, Dictionary<string, T> store)
    {
        _active = active;
        _store = store;
        _active.ActiveCharacterChanged += OnActiveCharacterChanged;
    }

    /// <summary>
    /// Entry for the currently active character, or null if no character is active.
    /// The entry is created on demand (a missing key lazily inserts a <c>new T()</c>).
    /// </summary>
    public T? Current
    {
        get
        {
            var name = _active.ActiveCharacterName;
            if (string.IsNullOrEmpty(name)) return null;
            if (!_store.TryGetValue(name, out var value))
            {
                value = new T();
                _store[name] = value;
            }
            return value;
        }
    }

    /// <summary>Entry for a specific character, creating it lazily if absent.</summary>
    public T GetFor(string characterName)
    {
        if (string.IsNullOrEmpty(characterName)) throw new ArgumentException("Character name required", nameof(characterName));
        if (!_store.TryGetValue(characterName, out var value))
        {
            value = new T();
            _store[characterName] = value;
        }
        return value;
    }

    /// <summary>Fires when <see cref="Current"/> transitions to a new character's entry.</summary>
    public event EventHandler? CurrentChanged;

    private void OnActiveCharacterChanged(object? sender, EventArgs e)
        => CurrentChanged?.Invoke(this, EventArgs.Empty);
}
