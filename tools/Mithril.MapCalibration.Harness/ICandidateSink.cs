namespace Mithril.Tools.MapCalibration.Harness;

/// <summary>
/// The channel a calibration method emits <see cref="CandidateRef"/>s into.
/// Interactive methods call <see cref="Emit"/> once per user action; batch
/// methods call <see cref="EmitBatch"/> after a scan. The session accepts each
/// candidate into its live ref set and re-solves.
/// </summary>
public interface ICandidateSink
{
    void Emit(CandidateRef candidate);

    void EmitBatch(IEnumerable<CandidateRef> candidates);
}
