using Mithril.Shared.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace Mithril.Shared.Character;

/// <summary>
/// Stamps <see cref="CharacterPresence.LastActiveAt"/> for the outgoing character on every
/// active-character switch, and for the currently-active character on graceful shutdown.
/// Coupled to <see cref="IActiveCharacterService"/> so no module ever has to derive this
/// timing independently.
/// </summary>
public sealed class CharacterPresenceService : IHostedService, ICharacterPresenceService, IDisposable
{
    private readonly IActiveCharacterService _active;
    private readonly PerCharacterStore<CharacterPresence> _store;
    private readonly IDiagnosticsSink? _diag;

    private (string Name, string Server)? _tracked;

    public CharacterPresenceService(
        IActiveCharacterService active,
        PerCharacterStore<CharacterPresence> store,
        IDiagnosticsSink? diag = null)
    {
        _active = active;
        _store = store;
        _diag = diag;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _tracked = CurrentKey();
        _active.ActiveCharacterChanged += OnActiveCharacterChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _active.ActiveCharacterChanged -= OnActiveCharacterChanged;
        StampCurrent();
        return Task.CompletedTask;
    }

    public DateTimeOffset? GetLastActiveAt(string character, string server)
    {
        try { return _store.Load(character, server).LastActiveAt; }
        catch (Exception ex)
        {
            _diag?.Warn("Presence", $"Read failed for {character}/{server}: {ex.Message}");
            return null;
        }
    }

    public void Dispose() => _active.ActiveCharacterChanged -= OnActiveCharacterChanged;

    private void OnActiveCharacterChanged(object? sender, EventArgs e)
    {
        // Stamp the outgoing character — not the incoming one.
        var outgoing = _tracked;
        _tracked = CurrentKey();
        if (outgoing is { } o) Stamp(o.Name, o.Server);
    }

    private void StampCurrent()
    {
        if (_tracked is { } key) Stamp(key.Name, key.Server);
    }

    private void Stamp(string character, string server)
    {
        try
        {
            var presence = _store.Load(character, server);
            presence.LastActiveAt = DateTimeOffset.UtcNow;
            _store.Save(character, server, presence);
        }
        catch (Exception ex)
        {
            _diag?.Warn("Presence", $"Stamp failed for {character}/{server}: {ex.Message}");
        }
    }

    private (string, string)? CurrentKey()
    {
        var name = _active.ActiveCharacterName;
        var server = _active.ActiveServer;
        return string.IsNullOrEmpty(name) || string.IsNullOrEmpty(server) ? null : (name, server);
    }
}
