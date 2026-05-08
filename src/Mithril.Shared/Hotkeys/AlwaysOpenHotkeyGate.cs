using System.ComponentModel;

namespace Mithril.Shared.Hotkeys;

public sealed class AlwaysOpenHotkeyGate : IHotkeyGate
{
    public bool CanFire => true;
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}
