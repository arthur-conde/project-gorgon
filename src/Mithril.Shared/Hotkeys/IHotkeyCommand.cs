namespace Mithril.Shared.Hotkeys;

public interface IHotkeyCommand
{
    string Id { get; }
    string DisplayName { get; }
    string? Category { get; }
    HotkeyBinding? DefaultBinding { get; }
    Task ExecuteAsync(CancellationToken ct);

    // Default true: most commands act on game state and shouldn't fire when the
    // user has alt-tabbed to a browser. Overlay-toggle commands opt out so the
    // user can still peek at the map/inventory from out-of-app.
    bool RespectsFocusGate => true;
}
