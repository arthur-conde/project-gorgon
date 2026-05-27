using Microsoft.Extensions.Logging;
using Arda.Contracts;
using Arda.World.Player;
using Arda.World.Player.Events;
using Legolas.Domain;
using Mithril.Shared.Character;

namespace Legolas.Services;

/// <summary>
/// The player's <b>declared</b> position: an in-game map pin they dropped
/// labelled with their character name (or the fixed <see cref="SelfPinSentinel"/>
/// sentinel). <see cref="World"/> is the pin's exact engine-unit coordinate;
/// <see cref="ObservedAt"/> is when the declaration was seen (UTC). The
/// <see cref="Label"/> records which pin established this anchor so a
/// subsequent <c>ProcessMapPinRemove</c> at the same coords (rename = remove
/// + add at identical coords, or a coincidental overlap with another player's
/// pin) does not clear it.
/// </summary>
public readonly record struct CharacterPinFix(WorldCoord World, DateTimeOffset ObservedAt, string Label);

/// <summary>
/// Resolves a "this is where I am" self-declaration from the area's map pins.
/// </summary>
public interface ICharacterPinAnchor
{
    CharacterPinFix? Current { get; }

    /// <summary>True iff the pin label is the self-declaration convention
    /// (character name or <see cref="CharacterPinAnchor.SelfPinSentinel"/>).</summary>
    bool IsSelfPin(string? label);

    event Action? Changed;
}

/// <inheritdoc cref="ICharacterPinAnchor"/>
public sealed class CharacterPinAnchor : ICharacterPinAnchor, IDisposable
{
    public const string SelfPinSentinel = "@me";

    private readonly IActiveCharacterService _activeChar;
    private readonly IMapPinState _mapPinState;
    private readonly ILogger? _logger;
    private readonly IDisposable _pinAddedSub;
    private readonly IDisposable _pinRemovedSub;
    private readonly IDisposable _areaChangedSub;

    private readonly object _gate = new();
    private CharacterPinFix? _current;

    public event Action? Changed;

    public CharacterPinAnchor(
        IDomainEventSubscriber bus,
        IMapPinState mapPinState,
        IActiveCharacterService activeChar,
        ILogger? logger = null)
    {
        _mapPinState = mapPinState;
        _activeChar = activeChar;
        _logger = logger;
        _activeChar.ActiveCharacterChanged += OnActiveCharacterChanged;

        // Initial resolve from current pin state (replaces Snapshot replay)
        _current = ResolveFromPinState(DateTimeOffset.UtcNow);

        _pinAddedSub = bus.Subscribe<MapPinAdded>(OnPinAdded);
        _pinRemovedSub = bus.Subscribe<MapPinRemoved>(OnPinRemoved);
        _areaChangedSub = bus.Subscribe<AreaChanged>(OnAreaChanged);
    }

    public CharacterPinFix? Current
    {
        get { lock (_gate) return _current; }
    }

    public bool IsSelfPin(string? label) => Matches(label, _activeChar.ActiveCharacterName);

    private static bool Matches(string? label, string? activeName)
    {
        var trimmed = label?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return false;
        if (string.Equals(trimmed, SelfPinSentinel, StringComparison.OrdinalIgnoreCase)) return true;
        return !string.IsNullOrWhiteSpace(activeName)
            && string.Equals(trimmed, activeName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private CharacterPinFix? ResolveFromPinState(DateTimeOffset observedAt)
    {
        var name = _activeChar.ActiveCharacterName?.Trim();
        MapPinEntry? sentinel = null;
        foreach (var p in _mapPinState.Pins)
        {
            var label = p.Label?.Trim();
            if (string.IsNullOrEmpty(label)) continue;
            if (!string.IsNullOrWhiteSpace(name)
                && string.Equals(label, name, StringComparison.OrdinalIgnoreCase))
                return new CharacterPinFix(new WorldCoord(p.X, 0, p.Z), observedAt, label);
            if (sentinel is null
                && string.Equals(label, SelfPinSentinel, StringComparison.OrdinalIgnoreCase))
                sentinel = p;
        }
        return sentinel is { } s
            ? new CharacterPinFix(new WorldCoord(s.X, 0, s.Z), observedAt, s.Label?.Trim() ?? SelfPinSentinel)
            : null;
    }

    private void OnPinAdded(MapPinAdded evt)
    {
        bool changed;
        var at = evt.Metadata.Timestamp ?? evt.Metadata.ReadOn;
        lock (_gate)
        {
            CharacterPinFix? next;
            if (Matches(evt.Label, _activeChar.ActiveCharacterName))
                next = new CharacterPinFix(new WorldCoord(evt.X, 0, evt.Z), at, evt.Label?.Trim() ?? string.Empty);
            else
                next = _current;
            changed = PresenceOrCoordChanged(_current, next);
            _current = next;
        }
        if (changed)
        {
            _logger?.LogTrace(_current is { } f ? $"declared @ ({f.World.X:0},{f.World.Z:0})" : "cleared");
            Changed?.Invoke();
        }
    }

    private void OnPinRemoved(MapPinRemoved evt)
    {
        bool changed;
        var at = evt.Metadata.Timestamp ?? evt.Metadata.ReadOn;
        lock (_gate)
        {
            CharacterPinFix? next;
            // Identity is (Label, X, Z) — coords alone collide on rename
            // (remove + add at identical coords) and on coincidental overlap
            // with another player's pin. Per pg_map_pin_log_grammar.
            var removedLabel = evt.Label?.Trim() ?? string.Empty;
            if (_current is { } cur
                && evt.X == cur.World.X && evt.Z == cur.World.Z
                && string.Equals(removedLabel, cur.Label, StringComparison.OrdinalIgnoreCase))
                next = ResolveFromPinState(at);
            else
                next = _current;
            changed = PresenceOrCoordChanged(_current, next);
            _current = next;
        }
        if (changed)
        {
            _logger?.LogTrace(_current is { } f ? $"declared @ ({f.World.X:0},{f.World.Z:0})" : "cleared");
            Changed?.Invoke();
        }
    }

    private void OnAreaChanged(AreaChanged evt)
    {
        bool changed;
        lock (_gate)
        {
            changed = _current is not null;
            _current = null;
        }
        if (changed)
        {
            _logger?.LogTrace("cleared");
            Changed?.Invoke();
        }
    }

    private void OnActiveCharacterChanged(object? sender, EventArgs e)
    {
        bool changed;
        lock (_gate)
        {
            var next = ResolveFromPinState(DateTimeOffset.UtcNow);
            changed = PresenceOrCoordChanged(_current, next);
            _current = next;
        }
        if (changed) Changed?.Invoke();
    }

    private static bool PresenceOrCoordChanged(CharacterPinFix? a, CharacterPinFix? b)
    {
        if (a is null != b is null) return true;
        if (a is { } x && b is { } y)
            return x.World.X != y.World.X || x.World.Z != y.World.Z;
        return false;
    }

    public void Dispose()
    {
        _pinAddedSub.Dispose();
        _pinRemovedSub.Dispose();
        _areaChangedSub.Dispose();
        _activeChar.ActiveCharacterChanged -= OnActiveCharacterChanged;
    }
}
