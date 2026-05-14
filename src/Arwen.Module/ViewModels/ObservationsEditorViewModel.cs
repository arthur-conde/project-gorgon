namespace Arwen.ViewModels;

/// <summary>
/// Marker VM that wraps <see cref="CalibrationViewModel"/> so the
/// "Edit Observations" tab can be distinguished from the "Calibration" tab
/// by DataTemplate type even though both surfaces share the same underlying
/// view-model state. The <c>FavorView</c>'s DataTemplate unwraps this back
/// to the inner <see cref="Calibration"/> instance for the actual
/// <c>ObservationsEditorTab</c> view, so existing <c>{Binding X}</c>
/// expressions inside that view continue to resolve against
/// <see cref="CalibrationViewModel"/> unchanged.
/// </summary>
public sealed record ObservationsEditorViewModel(CalibrationViewModel Calibration);
