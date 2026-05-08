using System.ComponentModel;

namespace Mithril.Shared.Hotkeys;

/// <summary>
/// Optional secondary interface for commands whose registration depends on
/// runtime state beyond foreground focus (e.g. Legolas Pin Nudge only being
/// useful while a pin is positionally uncertain). Composes with
/// <see cref="IHotkeyGate"/>: a binding registers iff the focus gate allows
/// it AND <see cref="IsRegistrable"/> is true.
///
/// Commands signal state changes via <see cref="INotifyPropertyChanged"/>;
/// <see cref="HotkeyService"/> subscribes during construction and re-applies
/// registration on each change.
/// </summary>
public interface IGatedHotkeyCommand : IHotkeyCommand, INotifyPropertyChanged
{
    bool IsRegistrable { get; }
}
