using Microsoft.Extensions.Logging;
using Arda.Composition;
using Arda.Contracts;
using Arda.World.Player.Events;
using Mithril.Shared.Character;
using Mithril.Shared.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace Mithril.Shell.DependencyInjection;

/// <summary>
/// Subscribes to Arda domain events and feeds character identity into
/// <see cref="IActiveCharacterService"/>. Replaces the legacy implementation
/// that regex-matched raw <c>Player.log</c> lines via <c>IPlayerLogStream</c>.
///
/// <para>Subscribes to <see cref="SessionStarted"/> for the character name
/// (available immediately from Player.log) and <see cref="SessionEstablished"/>
/// for the server (available once the chat banner fuses). The service suppresses
/// no-op writes so late replays and duplicate events are safe.</para>
/// </summary>
internal sealed class ActiveCharacterLogSynchronizer : IHostedService, IDisposable
{
    private readonly IDomainEventSubscriber _bus;
    private readonly IActiveCharacterService _active;
    private readonly ILogger? _logger;

    private IDisposable? _sessionSub;
    private IDisposable? _establishedSub;
    private bool _disposed;

    public ActiveCharacterLogSynchronizer(
        IDomainEventSubscriber bus,
        IActiveCharacterService active,
        ILogger? logger = null)
    {
        _bus = bus;
        _active = active;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger?.LogDiagnosticInfo("ActiveChar", "Subscribing to Arda SessionStarted + SessionEstablished");
        _sessionSub = _bus.Subscribe<SessionStarted>(OnSessionStarted);
        _establishedSub = _bus.Subscribe<SessionEstablished>(OnSessionEstablished);
        return Task.CompletedTask;
    }

    private void OnSessionStarted(SessionStarted evt)
    {
        var server = ResolveServer(evt.CharacterName);
        _active.SetActiveCharacter(evt.CharacterName, server);
    }

    private void OnSessionEstablished(SessionEstablished evt)
    {
        _active.SetActiveCharacter(
            evt.Session.CharacterName,
            evt.Session.Server ?? ResolveServer(evt.Session.CharacterName));
    }

    /// <summary>Best-effort: prefer an existing snapshot's server, else the persisted server.</summary>
    private string ResolveServer(string name)
    {
        var match = _active.Characters.FirstOrDefault(c =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match.Server;
        return _active.ActiveServer ?? "";
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
        _establishedSub?.Dispose();
    }
}
