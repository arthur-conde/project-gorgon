namespace Mithril.Tools.MapCalibration.Harness;

/// <summary>
/// A pluggable producer of calibration references. Every method differs only in
/// how it produces <c>(world, texture)</c> pairs; everything downstream (solve,
/// verify, correct, commit) is the harness's job. A method is
/// <see cref="Activate"/>d for a session and emits candidates via the sink until
/// the returned <see cref="IDisposable"/> is disposed. This unifies interactive
/// producers (manual-click stays subscribed to surface clicks) and batch
/// producers (green-pixel scans on a trigger).
/// </summary>
public interface ICalibrationMethod
{
    string Name { get; }

    string Description { get; }

    /// <summary>
    /// A WPF <c>UserControl</c> hosted in the shell's method panel (landmark
    /// picker / sliders / "scan" button), or null. <b>Always null in the
    /// headless core and tests</b> — typed as <see cref="object"/> so the core
    /// stays WPF-free.
    /// </summary>
    object? ConfigView { get; }

    /// <summary>
    /// Begins producing candidates for <paramref name="ctx"/> into
    /// <paramref name="sink"/>. Dispose the returned handle to detach the method.
    /// </summary>
    IDisposable Activate(CalibrationContext ctx, ICandidateSink sink);
}
