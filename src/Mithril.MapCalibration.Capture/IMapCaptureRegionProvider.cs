using System;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Provides + persists the single map-capture bbox (spec §7). The "draw map
/// bbox" hotkey and direct overlay move/resize both write here; the auto-attempt
/// reads <see cref="Current"/> to know whether a region has been framed.
/// </summary>
public interface IMapCaptureRegionProvider
{
    /// <summary><see langword="null"/> → no bbox set yet (gates the auto-attempt, spec §10/§11).</summary>
    CaptureRect? Current { get; }

    /// <summary>Persist a new bbox and raise <see cref="Changed"/>.</summary>
    void Set(CaptureRect rect);

    /// <summary>Raised after <see cref="Current"/> changes.</summary>
    event EventHandler? Changed;
}
