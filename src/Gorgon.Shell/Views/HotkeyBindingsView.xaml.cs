using System.Windows.Controls;
using Gorgon.Shared.Hotkeys.Controls;
using Gorgon.Shell.ViewModels;

namespace Gorgon.Shell.Views;

public partial class HotkeyBindingsView : UserControl
{
    public HotkeyBindingsView() { InitializeComponent(); AddHandler(HotkeyChipControl.LostKeyboardFocusEvent, new System.Windows.RoutedEventHandler((_, __) => { })); }
}
