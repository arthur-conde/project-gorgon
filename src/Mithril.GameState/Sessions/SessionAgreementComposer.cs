using Mithril.Shared.Diagnostics;
using Mithril.Shared.Modules;
using Mithril.WorldSim.Chat;

namespace Mithril.GameState.Sessions;

/// <summary>
/// View-layer composer (#633) that verifies the server identity carried by the
/// Player.log login banner agrees with the server identity carried by the chat
/// login banner. Per <see cref="Mithril.GameState.Sessions.IGameSessionService"/>
/// (Player.log side) and <see cref="IChatSessionService"/> (chat side), the two
/// streams self-scope independently (world-sim principle 7); a view-layer
/// composer is the place where their two banner observations meet and can be
/// cross-checked (world-sim principle 3 — no service spans both sources).
///
/// <para><b>Verification only.</b> Per #633 acceptance, the composer never
/// mutates either source's <c>GameSession</c> / <see cref="ChatSession"/>. The
/// Player.log-derived <c>GameSession.Server</c> remains the authoritative
/// field consumed by downstream <c>(Server, Character)</c>-scoped services;
/// the chat banner's server identity is purely a cross-check input.</para>
///
/// <para><b>Pairing.</b> The composer holds the most-recent <see cref="GameSession"/>
/// (from <see cref="IGameSessionService"/>) and the most-recent
/// <see cref="ChatSession"/> (from <see cref="IChatSessionService"/>). Each
/// time either side updates, it pairs against the other-side's most-recent
/// observation. A pair is a candidate for the agreement check only when both
/// observations name the same <c>Character</c> — when characters differ, the
/// chat side is presumed to be lagging the player side mid-character-swap and
/// the check is skipped (silent). When characters match and both sides carry
/// a non-null server identity, the server names are compared verbatim (no
/// canonicalisation — both sides parse the verbatim banner string, so
/// equality holds when PG's two banners agree).</para>
///
/// <para><b>Missing-side cases are silent.</b> Per #633 acceptance: cold-start
/// mid-PG-session can leave <see cref="GameSession.Server"/> null (L0 seed
/// skipped the preamble; the connect-event URL never reached
/// <c>GameSessionService</c>). The chat side may be unobserved entirely
/// (Mithril attached after PG's chat banner). Both sides may be null pre-
/// banner. None of these produce a warning — only a genuine disagreement
/// (both non-null, different identities, same character) does.</para>
///
/// <para><b>Deduplication.</b> Repeated bus emissions for the same logical
/// pairing (e.g. <see cref="IGameSessionService.SessionStarted"/> for a session
/// whose chat counterpart has already been compared) re-emit nothing — the
/// composer keys its last-emitted warning on
/// <c>(SessionId, ChatBannerTimestamp)</c>. A genuine re-disagreement (a new
/// chat banner observed against the same player session) fires once per new
/// chat banner.</para>
///
/// <para><b>Attention surface.</b> The composer implements
/// <see cref="IAttentionSource"/> with <see cref="ModuleId"/> = <c>"session-agreement"</c>
/// so a disagreement surfaces on <see cref="IAttentionAggregator"/> alongside
/// other shell-side attention signals (e.g. degraded log subscriptions). The
/// count is the number of distinct <c>(SessionId, ChatBannerTimestamp)</c>
/// disagreements observed this process lifetime — non-decreasing, since
/// disagreements are evidence of a genuine PG / data mismatch that warrants
/// investigation rather than self-clearing UX.</para>
///
/// <para><b>Mode.</b> Both upstream session services are mode-agnostic — they
/// publish during world replay identically to live. The agreement check is
/// also mode-agnostic; a disagreement is a structural fault in either source
/// or in PG's emission, equally significant during replay as live. The Warn
/// diagnostic is the user-visible surface; no audio / OS notification is
/// gated.</para>
/// </summary>
public sealed class SessionAgreementComposer : IAttentionSource, IDisposable
{
    /// <summary>
    /// Diagnostic category for every emission. Matches the spec — issue #633
    /// acceptance bullet 2 ("Warn diagnostic (category <c>"SessionAgreement"</c>)").
    /// </summary>
    public const string DiagnosticCategory = "SessionAgreement";

    /// <summary>
    /// <see cref="IAttentionSource.ModuleId"/> bucket id for this composer.
    /// Not a real module — uses the same convention as
    /// <see cref="Mithril.Shared.Logging.LogStreamAttentionSource"/>'s shared
    /// <c>"logging"</c> bucket.
    /// </summary>
    public const string AttentionSourceId = "session-agreement";

    private readonly IGameSessionService _playerSessions;
    private readonly IChatSessionService _chatSessions;
    private readonly IDiagnosticsSink? _diag;

    private readonly object _gate = new();
    private GameSession? _lastPlayer;
    private ChatSession? _lastChat;

    // Dedup key: (PlayerSessionId, ChatBannerAt). Each entry is one observed
    // disagreement; the count is the size of the set. Process-lifetime —
    // disagreements never self-clear.
    private readonly HashSet<(string PlayerSessionId, DateTimeOffset ChatBannerAt)> _emittedWarnings = new();

    private IDisposable? _playerSub;
    private IDisposable? _chatSub;
    private bool _started;
    private bool _disposed;

    public SessionAgreementComposer(
        IGameSessionService playerSessions,
        IChatSessionService chatSessions,
        IDiagnosticsSink? diag = null)
    {
        _playerSessions = playerSessions ?? throw new ArgumentNullException(nameof(playerSessions));
        _chatSessions = chatSessions ?? throw new ArgumentNullException(nameof(chatSessions));
        _diag = diag;
    }

    public string ModuleId => AttentionSourceId;
    public string DisplayLabel => "Session agreement — Player.log vs chat banner mismatch";

    public int Count
    {
        get { lock (_gate) return _emittedWarnings.Count; }
    }

    public event EventHandler? Changed;

    /// <summary>
    /// Attach to both session services. Idempotent — calling twice is safe.
    /// The DI extension registers this as a hosted service so attachment
    /// happens during host start, before the trailing
    /// <c>WorldMergerStartHostedService</c> (#696 Call 2) opens the
    /// world-merger drains.
    /// </summary>
    public void Start()
    {
        if (_started) return;
        _started = true;

        // IGameSessionService.Subscribe atomically replays Current before going
        // live; IChatSessionService.Subscribe does NOT replay Current per its
        // interface contract. We mirror that asymmetry by reading the chat
        // side's Current explicitly on attach (under the same lock that holds
        // the player subscribe so a chat-replay-then-player-subscribe race
        // doesn't drop a candidate pair).
        lock (_gate)
        {
            _lastChat = _chatSessions.Current;
            _playerSub = _playerSessions.Subscribe(OnPlayerSession);
            _chatSub = _chatSessions.Subscribe(OnChatSession);

            // If the chat side already had a banner at attach AND the player
            // subscribe just delivered the current player session, both
            // observations are now in place — run the check once.
            if (_lastChat is not null && _lastPlayer is not null)
            {
                CheckAgreementLocked();
            }
        }
    }

    private void OnPlayerSession(GameSession session)
    {
        lock (_gate)
        {
            _lastPlayer = session;
            CheckAgreementLocked();
        }
    }

    private void OnChatSession(ChatSession session)
    {
        lock (_gate)
        {
            _lastChat = session;
            CheckAgreementLocked();
        }
    }

    private void CheckAgreementLocked()
    {
        var player = _lastPlayer;
        var chat = _lastChat;

        // Missing-side cases are silent (acceptance bullet 4).
        if (player is null || chat is null) return;

        // Character mismatch means the chat side hasn't caught up to the most
        // recent player banner (mid-character-swap). Not a disagreement —
        // we're comparing the wrong logical pair. Silent.
        if (!string.Equals(player.CharacterName, chat.Character, StringComparison.Ordinal))
        {
            return;
        }

        // Player-side server identity unknown (cold-start mid-PG-session
        // skipped the preamble) — silent. See GameSession.Server XML doc for
        // the documented null-server path.
        var playerServerName = player.Server?.Name;
        if (string.IsNullOrEmpty(playerServerName)) return;

        // Chat-side server identity unknown — silent. (The chat banner always
        // carries Server verbatim, but an empty string indicates the parse
        // dropped the field; treat as missing rather than disagreeing.)
        if (string.IsNullOrEmpty(chat.Server)) return;

        if (string.Equals(playerServerName, chat.Server, StringComparison.Ordinal))
        {
            // Matching observation — silent (acceptance bullet 4).
            return;
        }

        // Genuine disagreement. Dedup by (PlayerSessionId, ChatBannerTimestamp)
        // so a re-emission of the same player session or the same chat banner
        // doesn't re-fire. A new chat banner for the same player session DOES
        // re-fire (different ChatBannerTimestamp).
        var dedupKey = (player.SessionId, chat.At);
        if (!_emittedWarnings.Add(dedupKey)) return;

        var message =
            $"Player.log banner server '{playerServerName}' disagrees with chat banner server '{chat.Server}' " +
            $"for character '{player.CharacterName}' (session id '{player.SessionId}', chat banner at {chat.At:O}). " +
            "The Player.log-derived server identity remains authoritative for downstream services; " +
            "this warning surfaces only as a cross-source consistency check.";
        _diag?.Warn(DiagnosticCategory, message);

        // IAttentionSource fires Changed under the gate's snapshot — handlers
        // typically marshal to the UI thread via the aggregator, so a quick
        // re-entry into Count is safe (Count itself takes the gate).
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _playerSub?.Dispose();
        _chatSub?.Dispose();
    }
}
