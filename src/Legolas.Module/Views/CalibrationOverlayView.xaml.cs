using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Mithril.Shared.Settings;
using Legolas.Controls;
using Legolas.Domain;
using Legolas.ViewModels;

namespace Legolas.Views;

/// <summary>
/// Standalone transparent, topmost click-capture window for per-area map
/// calibration. The user sees the in-game region map through it and clicks
/// where each picked landmark/NPC sits; those clicks feed
/// <see cref="CalibrationSessionViewModel"/> (no survey-FSM coupling).
///
/// Deliberately NOT click-through (unlike the survey overlay's optional mode):
/// the whole point is to capture clicks. <see cref="ClickThrough.ForceTopmost"/>
/// only keeps it above the game while it's open.
/// </summary>
public partial class CalibrationOverlayView : Window
{
    public CalibrationOverlayView()
    {
        InitializeComponent();
    }

    public CalibrationOverlayView(LegolasSettings settings, SettingsAutoSaver<LegolasSettings> saver) : this()
    {
        WindowLayoutBinder.Bind(this, settings.CalibrationOverlay, saver.Touch);
        Loaded += (_, _) => ClickThrough.ForceTopmost(this);
        Activated += (_, _) => ClickThrough.ForceTopmost(this);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not CalibrationSessionViewModel vm) return;
        // Only the transparent viewport background is a placement click — markers
        // are IsHitTestVisible=False, and the control panel is a separate opaque
        // dock that never routes here.
        if (e.OriginalSource is not FrameworkElement fe || !ReferenceEquals(fe, Viewport)) return;

        var pos = Mouse.GetPosition(Viewport);
        vm.PlaceSelectedAtCommand.Execute(new PixelPoint(pos.X, pos.Y));
        e.Handled = true;
    }

    /// <summary>
    /// Arrow keys fine-tune the selected placement (Shift = 5px). Skipped when
    /// focus is inside the area combo or a list — those need arrows for their
    /// own navigation; after a map click focus is on the window, which is the
    /// normal nudge case.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (DataContext is CalibrationSessionViewModel vm
            && vm.SelectedPlacement is not null
            && TryArrowDelta(e.Key, out var ux, out var uy)
            && !FocusIsInListOrCombo())
        {
            var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 5.0 : 1.0;
            vm.NudgeSelected(ux * step, uy * step);
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private static bool TryArrowDelta(Key key, out double dx, out double dy)
    {
        (dx, dy) = key switch
        {
            Key.Left => (-1.0, 0.0),
            Key.Right => (1.0, 0.0),
            Key.Up => (0.0, -1.0),
            Key.Down => (0.0, 1.0),
            _ => (0.0, 0.0),
        };
        return dx != 0 || dy != 0;
    }

    private static bool FocusIsInListOrCombo()
    {
        if (Keyboard.FocusedElement is not DependencyObject d) return false;
        for (var n = d; n is not null; n = VisualTreeHelper.GetParent(n))
            if (n is ListBox or ComboBox or ComboBoxItem)
                return true;
        return false;
    }
}
