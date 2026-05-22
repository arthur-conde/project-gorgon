using System.Text.RegularExpressions;
using Mithril.GameState.Chat;
using Mithril.GameState.WordsOfPower.Internal;
using Mithril.Shared.Character;
using Mithril.Shared.Diagnostics;
using Mithril.WorldSim;
using Mithril.WorldSim.Chat;
using Mithril.WorldSim.Player;

namespace Mithril.GameState.WordsOfPower;

/// <summary>
/// Canonical Words-of-Power surface (#603) — the cross-source view layer that
/// composes <see cref="IPlayerWorld"/>'s discovery folder with
/// <see cref="IChatWorld"/>'s player-chat folder. Per the world-simulator
/// design (principles 3 + 4 — no service spans both sources; cross-source
/// composition lives in views above the worlds), this view is the place where
/// discovery records meet spent observations.
///
/// <para><b>Composition.</b> The view subscribes to typed change events on
/// both world buses:
/// <list type="bullet">
///   <item><see cref="PlayerWordOfPowerDiscovered"/> from
///   <c>IPlayerWorld.Bus</c>.</item>
///   <item><see cref="ChatPlayerLineObserved"/> from
///   <c>IChatWorld.Bus</c>.</item>
/// </list>
/// For each chat line, the view runs an uppercase-token regex (length ≥ 4)
/// and looks each candidate up in the active character's discovery state. A
/// match flips the matching code's effective state to
/// <see cref="WordOfPowerKnowledge.Spent"/>; first-discovery materialises a
/// fresh Known entry.</para>
///
/// <para><b>Persistence.</b> The view's spent state lives in its own
/// per-character JSON ledger (<c>wop-spent.json</c>), distinct from the
/// discovery folder's <c>wop-discovery.json</c>. Spent state needs disk
/// because once burned, a code stays Spent forever — without persistence a
/// Mithril restart would reset every Known→Spent flip we already observed
/// (chat is live-only per the world-sim's <c>IChatLogStream</c> legacy
/// contract; the world-sim source replays from the latest banner but does not
/// retain history across PG re-logins).</para>
///
/// <para><b>Monotonic Spent — no TTL.</b> Unlike the inventory view, the WoP
/// view has no <c>PendingCorrelator</c> and no <c>IViewClock</c>: the join is
/// purely by code, and a chat utterance that lands hours after the discovery
/// (or weeks before, on a fresh discovery for an already-Spent code from
/// another character's burn — structurally impossible per PG's per-discovery
/// random code generation) still pairs correctly. Once
/// <c>LastSpentAt != null</c>, the view never clears it.</para>
///
/// <para><b>Observability gaps (accepted).</b> Codes the player never
/// discovered are invisible (no codebook entry to flip). Codes burned while
/// the player was offline are not observed (chat live-only after replay). The
/// module-internal user-override ledger handles the second case manually.</para>
/// </summary>
public sealed partial class WordOfPowerView : IWordOfPowerView, IDisposable
{
    // Uppercase letter runs of length ≥ 4 — the same shape the legacy chat
    // parser used. Real WoP codes seen: 6–11 chars. Random shouts in chat
    // (HOOOWL, MUAHAHAH, …) still scan but never match the codebook so they
    // never flip state.
    [GeneratedRegex(@"\b[A-Z]{4,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex UpperTokenRx();

    private readonly IPlayerWorld _playerWorld;
    private readonly IChatWorld _chatWorld;
    private readonly IPlayerWordOfPowerDiscoveryState _discoveryState;
    private readonly PerCharacterView<WordOfPowerViewState> _spentView;
    private readonly IDiagnosticsSink? _diag;
    private readonly ViewEventBus _bus = new();
    private readonly object _stateLock = new();

    private IDisposable? _discoveredSub;
    private IDisposable? _chatLineSub;
    private bool _started;
    private bool _disposed;

    public WordOfPowerView(
        IPlayerWorld playerWorld,
        IChatWorld chatWorld,
        IPlayerWordOfPowerDiscoveryState discoveryState,
        PerCharacterView<WordOfPowerViewState> spentView,
        IDiagnosticsSink? diag = null)
    {
        _playerWorld = playerWorld ?? throw new ArgumentNullException(nameof(playerWorld));
        _chatWorld = chatWorld ?? throw new ArgumentNullException(nameof(chatWorld));
        _discoveryState = discoveryState ?? throw new ArgumentNullException(nameof(discoveryState));
        _spentView = spentView ?? throw new ArgumentNullException(nameof(spentView));
        _diag = diag;

        // Re-raise CodebookChanged whenever the active character swaps — the
        // entire codebook + spent set rebinds.
        _spentView.CurrentChanged += (_, _) => CodebookChanged?.Invoke(this, EventArgs.Empty);
    }

    public IWorldEventBus Bus => _bus;

    public event EventHandler? CodebookChanged;

    public IReadOnlyCollection<WordOfPowerEntry> Entries
    {
        get
        {
            lock (_stateLock)
            {
                var spent = _spentView.Current?.SpentAt ?? new(StringComparer.Ordinal);
                var discoveries = _discoveryState.Discoveries;
                var entries = new List<WordOfPowerEntry>(discoveries.Count);
                foreach (var d in discoveries)
                {
                    spent.TryGetValue(d.Code, out var lastSpent);
                    entries.Add(new WordOfPowerEntry(
                        Code: d.Code,
                        EffectName: d.EffectName,
                        Description: d.Description,
                        DiscoveredAt: d.DiscoveredAt,
                        LastSpentAt: lastSpent == default ? null : lastSpent));
                }
                return entries;
            }
        }
    }

    public WordOfPowerEntry? TryGet(string code)
    {
        var d = _discoveryState.TryGet(code);
        if (d is null) return null;
        lock (_stateLock)
        {
            DateTime? lastSpent = null;
            var spent = _spentView.Current?.SpentAt;
            if (spent is not null && spent.TryGetValue(code, out var t)) lastSpent = t;
            return new WordOfPowerEntry(
                Code: d.Code,
                EffectName: d.EffectName,
                Description: d.Description,
                DiscoveredAt: d.DiscoveredAt,
                LastSpentAt: lastSpent);
        }
    }

    public bool IsSpent(string code)
    {
        lock (_stateLock)
        {
            var spent = _spentView.Current?.SpentAt;
            return spent is not null && spent.ContainsKey(code);
        }
    }

    /// <summary>
    /// Attach to both world buses. Idempotent — the registration hosted
    /// services call this once during host start; calling it twice is safe.
    /// </summary>
    public void Start()
    {
        if (_started) return;
        _started = true;

        _discoveredSub = _playerWorld.Bus.Subscribe<PlayerWordOfPowerDiscovered>(OnDiscovered);
        _chatLineSub = _chatWorld.Bus.Subscribe<ChatPlayerLineObserved>(OnChatLine);

        _diag?.Info("GameState.WordsOfPower.View",
            "WordOfPowerView subscribed to PlayerWorld + ChatWorld typed bus channels");
    }

    // ── PlayerWorld handler ──────────────────────────────────────────────

    private void OnDiscovered(Frame<PlayerWordOfPowerDiscovered> frame)
    {
        var p = frame.Payload;
        _bus.Publish(new Frame<WordOfPowerKnowledgeChanged>(
            frame.Timestamp,
            new WordOfPowerKnowledgeChanged(p.Code, WordOfPowerKnowledge.Known, p.Timestamp)));
        CodebookChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── ChatWorld handler ────────────────────────────────────────────────

    private void OnChatLine(Frame<ChatPlayerLineObserved> frame)
    {
        var p = frame.Payload;
        if (string.IsNullOrEmpty(p.Text)) return;

        // Scan candidate tokens; only matches in the discovery state flip
        // state. The match cost is bounded by the small set of distinct
        // uppercase tokens in any one chat line; most chat lines yield zero.
        foreach (Match tok in UpperTokenRx().Matches(p.Text))
        {
            var code = tok.Value;
            if (_discoveryState.TryGet(code) is null) continue;

            var ts = p.Timestamp;
            bool changed;
            lock (_stateLock)
            {
                var state = _spentView.Current;
                if (state is null)
                {
                    // No active character — nothing to persist against, and
                    // the discovery state would also be empty in this case.
                    continue;
                }
                if (state.SpentAt.ContainsKey(code))
                {
                    // Monotonic — already Spent, no-op.
                    changed = false;
                }
                else
                {
                    state.SpentAt[code] = ts;
                    Persist();
                    changed = true;
                }
            }

            if (changed)
            {
                _diag?.Trace("GameState.WordsOfPower.View",
                    $"Spent code={code} via chat utterance from speaker='{p.Speaker}' channel='{p.Channel}'");
                _bus.Publish(new Frame<WordOfPowerKnowledgeChanged>(
                    frame.Timestamp,
                    new WordOfPowerKnowledgeChanged(code, WordOfPowerKnowledge.Spent, ts)));
                CodebookChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void Persist()
    {
        try { _spentView.Save(); }
        catch { /* best-effort */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _discoveredSub?.Dispose();
        _chatLineSub?.Dispose();
    }
}
