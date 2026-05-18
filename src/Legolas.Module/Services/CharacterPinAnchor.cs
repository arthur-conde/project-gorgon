using Legolas.Domain;
using Mithril.GameState.Pins;
using Mithril.Shared.Character;
using Mithril.Shared.Diagnostics;

namespace Legolas.Services;

/// <summary>
/// The player's <b>declared</b> position: an in-game map pin they dropped
/// labelled with their character name (or the fixed <see cref="SelfPinSentinel"/>
/// sentinel). <see cref="World"/> is the pin's exact engine-unit coordinate
/// from <c>ProcessMapPinAdd</c> (#468); <see cref="ObservedAt"/> is when the
/// declaration was seen (UTC). A replay-sourced fix uses the subscribe instant
/// — the original drop time isn't in the log; re-dropping the pin refreshes it
/// precisely.
/// </summary>
public readonly record struct CharacterPinFix(WorldCoord World, DateTimeOffset ObservedAt);

/// <summary>
/// Resolves a "this is where I am" self-declaration from the area's map pins:
/// a pin whose label matches the active character name or the
/// <see cref="CharacterPinAnchor.SelfPinSentinel"/> sentinel. A deliberate,
/// opt-in self-position gesture — the in-game analogue of the #476 "Set my
/// position" click. Does <b>not</b> violate the #454 label-agnostic rule
/// (that forbids <em>inferring</em> game-entity pairings by name; this is the
/// user explicitly naming their own marker).
/// </summary>
public interface ICharacterPinAnchor
{
    /// <summary>The current declared position, or null when no matching pin is
    /// in the current area.</summary>
    CharacterPinFix? Current { get; }

    /// <summary>True iff this pin's label is the self-declaration convention
    /// (character name or <see cref="CharacterPinAnchor.SelfPinSentinel"/>).
    /// Used by the Motherlode feeder to prefer it without depending on
    /// cross-subscriber ordering.</summary>
    bool IsSelfPin(MapPin pin);

    /// <summary>Raised when <see cref="Current"/>'s presence or coordinate
    /// changes. Fired off the lock; marshal for UI work.</summary>
    event Action? Changed;
}

/// <inheritdoc cref="ICharacterPinAnchor"/>
public sealed class CharacterPinAnchor : ICharacterPinAnchor, IDisposable
{
    /// <summary>Label that always declares position regardless of character
    /// name — covers multi-box / the name not being resolved yet.</summary>
    public const string SelfPinSentinel = "@me";

    private readonly IActiveCharacterService _activeChar;
    private readonly IDiagnosticsSink? _diag;
    private readonly IDisposable _pinSub;

    private readonly object _gate = new();
    private IReadOnlyList<MapPin> _pins = Array.Empty<MapPin>();
    private CharacterPinFix? _current;

    public event Action? Changed;

    public CharacterPinAnchor(
        IPlayerPinTracker pinTracker,
        IActiveCharacterService activeChar,
        IDiagnosticsSink? diag = null)
    {
        _activeChar = activeChar;
        _diag = diag;
        _activeChar.ActiveCharacterChanged += OnActiveCharacterChanged;
        // Subscribe replays the current area set synchronously as a Snapshot.
        _pinSub = pinTracker.Subscribe(OnPins);
    }

    public CharacterPinFix? Current
    {
        get { lock (_gate) return _current; }
    }

    public bool IsSelfPin(MapPin pin) => Matches(pin, _activeChar.ActiveCharacterName);

    private static bool Matches(MapPin pin, string? activeName)
    {
        var label = pin.Label?.Trim();
        if (string.IsNullOrEmpty(label)) return false;
        if (string.Equals(label, SelfPinSentinel, StringComparison.OrdinalIgnoreCase)) return true;
        return !string.IsNullOrWhiteSpace(activeName)
            && string.Equals(label, activeName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    // Exact-name match outranks the @me sentinel; then set order. Returns the
    // chosen pin's fix, or null when nothing in the set matches.
    private CharacterPinFix? Resolve(IReadOnlyList<MapPin> pins, DateTimeOffset observedAt)
    {
        var name = _activeChar.ActiveCharacterName?.Trim();
        MapPin? sentinel = null;
        foreach (var p in pins)
        {
            var label = p.Label?.Trim();
            if (string.IsNullOrEmpty(label)) continue;
            if (!string.IsNullOrWhiteSpace(name)
                && string.Equals(label, name, StringComparison.OrdinalIgnoreCase))
                return new CharacterPinFix(new WorldCoord(p.X, 0, p.Z), observedAt);
            if (sentinel is null
                && string.Equals(label, SelfPinSentinel, StringComparison.OrdinalIgnoreCase))
                sentinel = p;
        }
        return sentinel is { } s
            ? new CharacterPinFix(new WorldCoord(s.X, 0, s.Z), observedAt)
            : null;
    }

    private void OnPins(PinSetChanged c)
    {
        bool changed;
        lock (_gate)
        {
            _pins = c.Pins;
            CharacterPinFix? next = c.Kind switch
            {
                // The freshly-added pin is the newest declaration if it matches;
                // a non-matching add leaves the current declaration intact.
                PinSetChange.Added when c.Pin is { } p && Matches(p, _activeChar.ActiveCharacterName)
                    => new CharacterPinFix(new WorldCoord(p.X, 0, p.Z), c.ObservedAt),
                PinSetChange.Added => _current,

                // Only re-resolve when the removed pin was the active one
                // (another @me/named pin may still remain).
                PinSetChange.Removed when _current is { } cur && c.Pin is { } rp
                    && rp.X == cur.World.X && rp.Z == cur.World.Z
                    => Resolve(c.Pins, c.ObservedAt),
                PinSetChange.Removed => _current,

                PinSetChange.AreaChanged => null,
                _ => Resolve(c.Pins, c.ObservedAt),   // Snapshot replay
            };
            changed = PresenceOrCoordChanged(_current, next);
            _current = next;
        }
        if (changed)
        {
            _diag?.Trace("Legolas.CharacterPin",
                _current is { } f ? $"declared @ ({f.World.X:0},{f.World.Z:0})" : "cleared");
            Changed?.Invoke();
        }
    }

    private void OnActiveCharacterChanged(object? sender, EventArgs e)
    {
        bool changed;
        lock (_gate)
        {
            var next = Resolve(_pins, DateTimeOffset.UtcNow);
            changed = PresenceOrCoordChanged(_current, next);
            _current = next;
        }
        if (changed) Changed?.Invoke();
    }

    // Ignore pure ObservedAt drift so a recompute that re-finds the same pin
    // doesn't storm consumers; presence flip or a coordinate move does.
    private static bool PresenceOrCoordChanged(CharacterPinFix? a, CharacterPinFix? b)
    {
        if (a is null != b is null) return true;
        if (a is { } x && b is { } y)
            return x.World.X != y.World.X || x.World.Z != y.World.Z;
        return false;
    }

    public void Dispose()
    {
        _pinSub.Dispose();
        _activeChar.ActiveCharacterChanged -= OnActiveCharacterChanged;
    }
}
