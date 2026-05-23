namespace Mithril.GameState.Areas;

/// <summary>
/// Discriminator on <see cref="PlayerAreaChanged"/> distinguishing late-
/// subscriber synthetic replays from genuine transitions. Mirrors the
/// existing GameState convention (<c>PinSetChange</c>, <c>WeatherChangeKind</c>):
/// late subscribers receive one synthetic <see cref="Snapshot"/> on attach so
/// they observe the same view already-attached handlers see, then live
/// transitions arrive as <see cref="Changed"/>.
///
/// <para><b>Bus surface vs legacy callback.</b>
/// <see cref="PlayerAreaTracker.Apply"/> only returns
/// <see cref="Changed"/>-kind events, so consumers subscribed via
/// <c>IPlayerWorld.Bus.Subscribe&lt;PlayerAreaChanged&gt;</c> see live
/// transitions only — the snapshot replay is synthesized inside the legacy
/// <see cref="IPlayerAreaState.Subscribe"/> path and never crosses the world
/// boundary.</para>
/// </summary>
public enum PlayerAreaChangeKind
{
    /// <summary>
    /// Synthetic notification fired once at
    /// <see cref="IPlayerAreaState.Subscribe"/> attach time. Carries the
    /// current area as <see cref="PlayerAreaChanged.Current"/> with
    /// <see cref="PlayerAreaChanged.Previous"/> = <c>null</c>; stamped with
    /// the most-recent envelope timestamp the tracker has applied (or
    /// <see cref="DateTimeOffset.MinValue"/> when nothing has been observed
    /// yet — late subscribers can short-circuit on <c>Current == null</c>).
    /// Never published on the world bus.
    /// </summary>
    Snapshot,

    /// <summary>
    /// Genuine area transition observed from a <c>LOADING LEVEL</c> line.
    /// Carries the prior area as <see cref="PlayerAreaChanged.Previous"/>
    /// and the new area as <see cref="PlayerAreaChanged.Current"/>;
    /// <see cref="PlayerAreaChanged.At"/> is the parsed log-line instant
    /// (never wall-clock — principle 13). Published on the world bus.
    /// </summary>
    Changed,
}
