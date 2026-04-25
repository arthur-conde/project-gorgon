namespace Mithril.Shared.Hotkeys;

public interface IHotkeyCommand
{
    string Id { get; }
    string DisplayName { get; }
    string? Category { get; }
    HotkeyBinding? DefaultBinding { get; }
    Task ExecuteAsync(CancellationToken ct);
}
