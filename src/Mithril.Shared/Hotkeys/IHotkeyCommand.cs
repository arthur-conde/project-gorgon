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

    /// <summary>
    /// Hide this command from the Hotkeys settings UI unless
    /// <c>ShellSettings.DeveloperMode</c> is on. Mirrors <see cref="Mithril.Shared.Modules.IMithrilModule.IsDeveloperOnly"/>
    /// for module-level visibility — diagnostic commands like the perf
    /// harness shouldn't clutter the binding list for player-facing
    /// installs. Existing bindings still execute at runtime regardless of
    /// this flag, so a user who toggles dev mode off keeps any bindings
    /// they previously made.
    /// </summary>
    bool IsDeveloperOnly => false;
}
