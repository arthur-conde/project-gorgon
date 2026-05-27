using Microsoft.Extensions.Logging;
using Arda.Contracts;
using Arda.World.Chat.Events;
using Arda.World.Player.Events;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Modules;
using Microsoft.Extensions.Hosting;

namespace Mithril.Shell.DependencyInjection;

/// <summary>
/// Cross-source session identity agreement check. Subscribes to Arda
/// <see cref="SessionStarted"/> (player log) and <see cref="ChatSessionIdentified"/>
/// (chat log) domain events and warns when the two sources disagree on server
/// identity for the same character.
///
/// <para>Today the player-side <see cref="SessionStarted"/> carries no server
/// field, so the check is always silent. When a future Arda handler adds a
/// connected-event with server identity, extend this composer to consume it.</para>
///
/// <para>Implements <see cref="IAttentionSource"/> so disagreements surface on
/// the shell attention aggregator (sidebar badge, tray menu). Count is
/// non-decreasing — disagreements are evidence of a structural fault and do not
/// self-clear.</para>
/// </summary>
internal sealed class SessionAgreementComposer : IHostedService, IAttentionSource, IDisposable
{
    public const string DiagnosticCategory = "SessionAgreement";
    public const string AttentionSourceId = "session-agreement";

    private readonly IDomainEventSubscriber _bus;
    private readonly ILogger? _logger;

    private readonly object _gate = new();
    private string? _playerCharacter;
    private string? _playerServer;
    private string? _sessionId;
    private string? _chatCharacter;
    private string? _chatServer;
    private DateTimeOffset _chatBannerAt;

    private readonly HashSet<(string SessionId, DateTimeOffset ChatBannerAt)> _emittedWarnings = new();

    private IDisposable? _sessionSub;
    private IDisposable? _chatSub;
    private bool _disposed;

    public SessionAgreementComposer(IDomainEventSubscriber bus, ILogger? logger = null)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _logger = logger;
    }

    public string ModuleId => AttentionSourceId;
    public string DisplayLabel => "Session agreement — Player.log vs chat banner mismatch";

    public int Count
    {
        get { lock (_gate) return _emittedWarnings.Count; }
    }

    public event EventHandler? Changed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionSub = _bus.Subscribe<SessionStarted>(OnSessionStarted);
        _chatSub = _bus.Subscribe<ChatSessionIdentified>(OnChatSession);
        return Task.CompletedTask;
    }

    private void OnSessionStarted(SessionStarted evt)
    {
        lock (_gate)
        {
            _playerCharacter = evt.CharacterName;
            // Arda does not parse the Player.log preamble (Servers: [...],
            // EVENT(Ok): connected) — server identity comes from the chat
            // banner or the IActiveCharacterService export fallback.
            // See #511 deliverable 7 for the deferred preamble-recovery work.
            _playerServer = null;
            var loggedInAt = evt.Metadata.Timestamp ?? evt.Metadata.ReadOn;
            _sessionId = $"{evt.CharacterName}:{loggedInAt:yyyyMMddHHmmss}";
            CheckAgreementLocked();
        }
    }

    private void OnChatSession(ChatSessionIdentified evt)
    {
        lock (_gate)
        {
            _chatCharacter = evt.Character;
            _chatServer = evt.Server;
            _chatBannerAt = evt.Metadata.Timestamp ?? evt.Metadata.ReadOn;
            CheckAgreementLocked();
        }
    }

    private void CheckAgreementLocked()
    {
        if (_playerCharacter is null || _chatCharacter is null) return;

        if (!string.Equals(_playerCharacter, _chatCharacter, StringComparison.Ordinal))
            return;

        if (string.IsNullOrEmpty(_playerServer)) return;
        if (string.IsNullOrEmpty(_chatServer)) return;

        if (string.Equals(_playerServer, _chatServer, StringComparison.Ordinal))
            return;

        var sid = _sessionId ?? _playerCharacter;
        var dedupKey = (sid, _chatBannerAt);
        if (!_emittedWarnings.Add(dedupKey)) return;

        _logger?.LogDiagnosticWarn(DiagnosticCategory,
            $"Player.log banner server '{_playerServer}' disagrees with chat banner server '{_chatServer}' " +
            $"for character '{_playerCharacter}' (session id '{sid}', chat banner at {_chatBannerAt:O}). " +
            "The Player.log-derived server identity remains authoritative for downstream services; " +
            "this warning surfaces only as a cross-source consistency check.");

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sessionSub?.Dispose();
        _chatSub?.Dispose();
    }
}
