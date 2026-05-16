namespace Celebrimbor.ViewModels;

public enum CelebrimborViewMode
{
    // Crafting wizard (sequential: build a list → shop for it).
    Picker,
    Shopping,

    // Plans — a peer area, not a wizard step: an independent library of saved
    // leveling plans you walk. Reached from outside the wizard (Elrond hand-off,
    // file import) and feeds back into it via "Send phase to craft list" (#228).
    Plans,
}
