namespace Gorgon.Shared.Hotkeys;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Ctrl = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

public sealed record HotkeyBinding(string CommandId, uint VirtualKey, HotkeyModifiers Modifiers);
