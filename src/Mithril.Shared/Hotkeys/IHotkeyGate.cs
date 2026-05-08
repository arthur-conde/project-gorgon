using System.ComponentModel;

namespace Mithril.Shared.Hotkeys;

/// <summary>
/// Controls whether <see cref="HotkeyService"/> currently holds its system-wide
/// Win32 hotkey registrations. When <see cref="CanFire"/> flips, the service
/// unregisters or re-registers so other apps can receive the keystrokes the
/// rest of the time. Default registration (<see cref="AlwaysOpenHotkeyGate"/>)
/// keeps everything always-on; Legolas swaps in a foreground-aware gate.
/// </summary>
public interface IHotkeyGate : INotifyPropertyChanged
{
    bool CanFire { get; }
}
