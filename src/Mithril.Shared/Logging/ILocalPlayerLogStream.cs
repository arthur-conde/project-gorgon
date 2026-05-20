namespace Mithril.Shared.Logging;

/// <summary>
/// L0.5 (#532) public surface for the LocalPlayer pipe — typed
/// <see cref="LocalPlayerLogLine"/> emissions classified from L0's
/// <see cref="IPlayerLogStream"/> Player.log feed. All current GameState
/// state-rebuilders and reactive module ingestion services that today
/// consume <see cref="IPlayerLogStream"/> + match <c>LocalPlayer:</c>
/// will migrate to this stream as part of #511 deliverable 5; until then
/// L0.5 ships dark alongside the existing <see cref="IPlayerLogStream"/>
/// surface.
/// </summary>
public interface ILocalPlayerLogStream
{
    IAsyncEnumerable<LocalPlayerLogLine> SubscribeAsync(CancellationToken ct);
}
