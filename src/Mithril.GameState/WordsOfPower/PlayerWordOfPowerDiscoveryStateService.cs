using Mithril.Shared.Character;
using Mithril.Shared.Diagnostics;
using Mithril.WorldSim;

namespace Mithril.GameState.WordsOfPower;

/// <summary>
/// Per-character folder for Player.log Words-of-Power discoveries (#603).
/// Folds <see cref="WordOfPowerDiscoveryFrame"/> frames into the active
/// character's persistent codebook (<see cref="PerCharacterView{T}"/>) and
/// emits <see cref="PlayerWordOfPowerDiscovered"/> on the PlayerWorld bus for
/// each genuinely-new code. Re-observations of an already-known code are
/// elided (PG replays <c>ProcessBook</c> on login replay and the persistent
/// state holds across restarts).
///
/// <para><b>World-simulator role.</b> Registered with <c>IPlayerWorld</c> as
/// an <see cref="IFolder{WordOfPowerDiscoveryFrame}"/> by the GameState DI
/// extension. A sibling
/// <see cref="Producers.PlayerWordOfPowerDiscoveryFrameProducer"/> owns the L1
/// LocalPlayer subscription and feeds frames into the world's merger.</para>
///
/// <para><b>Character switching.</b> Mutations apply to whichever character is
/// active when the frame is applied. When no character is active (the
/// <see cref="PerCharacterView{T}.Current"/> is <c>null</c>), the folder
/// drops the frame and emits no change event — the view-side
/// <see cref="WordOfPowerView"/> stays consistent with the on-disk state.
/// Character switching is observed via the
/// <see cref="PerCharacterView{T}.CurrentChanged"/> event, which fires after
/// the active character changes; the folder doesn't subscribe to it because
/// it never holds in-memory cross-character state.</para>
///
/// <para><b>Threading.</b> The world's merger drives <see cref="Apply"/>
/// serially; folder mutations and the
/// <see cref="PerCharacterView{T}"/> Save call run under <see cref="_lock"/>.</para>
/// </summary>
public sealed class PlayerWordOfPowerDiscoveryStateService
    : IFolder<WordOfPowerDiscoveryFrame>, IPlayerWordOfPowerDiscoveryState
{
    private readonly PerCharacterView<PlayerWordOfPowerDiscoveryStateData> _view;
    private readonly IDiagnosticsSink? _diag;
    private readonly object _lock = new();

    public PlayerWordOfPowerDiscoveryStateService(
        PerCharacterView<PlayerWordOfPowerDiscoveryStateData> view,
        IDiagnosticsSink? diag = null)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _diag = diag;
    }

    public IReadOnlyCollection<DiscoveryRecord> Discoveries
    {
        get
        {
            var state = _view.Current;
            if (state is null) return Array.Empty<DiscoveryRecord>();
            lock (_lock) return state.Discoveries.Values.ToArray();
        }
    }

    public DiscoveryRecord? TryGet(string code)
    {
        var state = _view.Current;
        if (state is null) return null;
        lock (_lock) return state.Discoveries.GetValueOrDefault(code);
    }

    public IReadOnlyList<IChangeEvent> Apply(Frame<WordOfPowerDiscoveryFrame> frame, IWorldClock clock)
    {
        _ = clock;
        var state = _view.Current;
        if (state is null)
        {
            _diag?.Trace("GameState.WordsOfPower.Discovery",
                $"Discovery for code={frame.Payload.Code} dropped — no active character");
            return Array.Empty<IChangeEvent>();
        }

        var payload = frame.Payload;
        var ts = frame.Timestamp.UtcDateTime;
        lock (_lock)
        {
            if (state.Discoveries.ContainsKey(payload.Code))
            {
                // Defensive — PG replays ProcessBook on login replay and the
                // persistent state covers cold start. The structural
                // impossibility of code re-use means a true rediscovery is
                // not expected; suppress the emit silently.
                return Array.Empty<IChangeEvent>();
            }

            state.Discoveries[payload.Code] = new DiscoveryRecord
            {
                Code = payload.Code,
                EffectName = payload.EffectName,
                Description = payload.Description,
                DiscoveredAt = ts,
            };
            Persist();

            _diag?.Trace("GameState.WordsOfPower.Discovery",
                $"Discovered code={payload.Code} effect='{payload.EffectName}' (total={state.Discoveries.Count})");

            return new IChangeEvent[]
            {
                new PlayerWordOfPowerDiscovered(payload.Code, payload.EffectName, payload.Description, ts),
            };
        }
    }

    private void Persist()
    {
        try { _view.Save(); }
        catch { /* best-effort persistence — same policy as other folders */ }
    }
}
