using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Gorgon.Shared.Hotkeys.Controls;

public enum HotkeyChipState { Idle, Capturing, Conflict, Error }

public sealed class HotkeyChipControl : Control
{
    static HotkeyChipControl()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(HotkeyChipControl),
            new FrameworkPropertyMetadata(typeof(HotkeyChipControl)));
        FocusableProperty.OverrideMetadata(typeof(HotkeyChipControl),
            new FrameworkPropertyMetadata(true));
    }

    public static readonly DependencyProperty BindingProperty =
        DependencyProperty.Register(nameof(Binding), typeof(HotkeyBinding), typeof(HotkeyChipControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBindingChanged));

    public HotkeyBinding? Binding
    {
        get => (HotkeyBinding?)GetValue(BindingProperty);
        set => SetValue(BindingProperty, value);
    }

    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(nameof(State), typeof(HotkeyChipState), typeof(HotkeyChipControl),
            new PropertyMetadata(HotkeyChipState.Idle));

    public HotkeyChipState State
    {
        get => (HotkeyChipState)GetValue(StateProperty);
        private set => SetValue(StateProperty, value);
    }

    public static readonly DependencyProperty DisplayTextProperty =
        DependencyProperty.Register(nameof(DisplayText), typeof(string), typeof(HotkeyChipControl),
            new PropertyMetadata("(unbound)"));

    public string DisplayText
    {
        get => (string)GetValue(DisplayTextProperty);
        private set => SetValue(DisplayTextProperty, value);
    }

    public static readonly DependencyProperty HotkeyServiceProperty =
        DependencyProperty.Register(nameof(HotkeyService), typeof(IHotkeyService), typeof(HotkeyChipControl));

    public IHotkeyService? HotkeyService
    {
        get => (IHotkeyService?)GetValue(HotkeyServiceProperty);
        set => SetValue(HotkeyServiceProperty, value);
    }

    public event EventHandler<HotkeyBinding?>? BindingCommitted;

    private IDisposable? _captureScope;
    private HotkeyBinding? _previousBinding;

    private static void OnBindingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((HotkeyChipControl)d).RefreshDisplay();
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        RefreshDisplay();
    }

    private void RefreshDisplay()
    {
        if (State == HotkeyChipState.Capturing) { DisplayText = "press keys…"; return; }
        DisplayText = Binding is null ? "(unbound)" : Format(Binding);
    }

    public static string Format(HotkeyBinding b)
    {
        var parts = new List<string>(4);
        if ((b.Modifiers & HotkeyModifiers.Ctrl) != 0) parts.Add("Ctrl");
        if ((b.Modifiers & HotkeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((b.Modifiers & HotkeyModifiers.Shift) != 0) parts.Add("Shift");
        if ((b.Modifiers & HotkeyModifiers.Win) != 0) parts.Add("Win");
        var key = KeyInterop.KeyFromVirtualKey((int)b.VirtualKey);
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (State != HotkeyChipState.Capturing) StartCapture();
        Focus();
        e.Handled = true;
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (State == HotkeyChipState.Capturing) CancelCapture();
    }

    private void StartCapture()
    {
        _previousBinding = Binding;
        _captureScope = HotkeyService?.BeginCaptureSession();
        State = HotkeyChipState.Capturing;
        RefreshDisplay();
    }

    private void EndCapture()
    {
        _captureScope?.Dispose();
        _captureScope = null;
        State = HotkeyChipState.Idle;
        RefreshDisplay();
    }

    private void CancelCapture()
    {
        Binding = _previousBinding;
        EndCapture();
    }

    private void Commit(HotkeyBinding? newBinding)
    {
        Binding = newBinding;
        EndCapture();
        BindingCommitted?.Invoke(this, newBinding);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (State != HotkeyChipState.Capturing) { base.OnPreviewKeyDown(e); return; }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape) { CancelCapture(); e.Handled = true; return; }
        if (IsModifierOnly(key)) { RefreshDisplay(); e.Handled = true; return; }
        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            Commit(null);
            e.Handled = true;
            return;
        }
        if (IsLockKey(key)) { e.Handled = true; return; } // ignore

        var mods = HotkeyModifiers.None;
        var k = Keyboard.Modifiers;
        if ((k & ModifierKeys.Control) != 0) mods |= HotkeyModifiers.Ctrl;
        if ((k & ModifierKeys.Alt) != 0) mods |= HotkeyModifiers.Alt;
        if ((k & ModifierKeys.Shift) != 0) mods |= HotkeyModifiers.Shift;
        if ((k & ModifierKeys.Windows) != 0) mods |= HotkeyModifiers.Win;

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        var commandId = (DataContext as HotkeyRowViewModelBase)?.CommandId;
        Commit(new HotkeyBinding(commandId ?? "", vk, mods));
        e.Handled = true;
    }

    private static bool IsModifierOnly(Key k) =>
        k is Key.LeftCtrl or Key.RightCtrl
          or Key.LeftAlt or Key.RightAlt
          or Key.LeftShift or Key.RightShift
          or Key.LWin or Key.RWin;

    private static bool IsLockKey(Key k) =>
        k is Key.CapsLock or Key.NumLock or Key.Scroll;
}

/// <summary>
/// Marker base so the chip control can pull a CommandId off DataContext
/// without taking a hard dependency on the shell's row VM type.
/// </summary>
public abstract class HotkeyRowViewModelBase
{
    public abstract string CommandId { get; }
}
