using System.Text.RegularExpressions;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Microsoft.Extensions.Hosting;

namespace Mithril.Shared.Character;

/// <summary>
/// Subscribes to <see cref="IPlayerLogStream"/> and feeds
/// <c>LocalPlayer: ProcessAddPlayer</c> events into <see cref="IActiveCharacterService"/>.
/// This is the only place the character-name regex lives — modules should not
/// re-parse login events. Single-shot: the service suppresses no-op writes
/// so late replays are safe.
/// </summary>
public sealed partial class ActiveCharacterLogSynchronizer : BackgroundService
{
    [GeneratedRegex(@"LocalPlayer:\s*ProcessAddPlayer\([^,]+,\s*[^,]+,\s*""[^""]*"",\s*""([^""]+)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex AddPlayerRx();

    private readonly IPlayerLogStream _stream;
    private readonly IActiveCharacterService _active;
    private readonly IDiagnosticsSink? _diag;

    public ActiveCharacterLogSynchronizer(
        IPlayerLogStream stream,
        IActiveCharacterService active,
        IDiagnosticsSink? diag = null)
    {
        _stream = stream;
        _active = active;
        _diag = diag;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _diag?.Info("ActiveChar", "Subscribing to Player.log for ProcessAddPlayer events");
        await foreach (var raw in _stream.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!raw.Line.Contains("ProcessAddPlayer", StringComparison.Ordinal)) continue;
            var m = AddPlayerRx().Match(raw.Line);
            if (!m.Success) continue;

            var name = m.Groups[1].Value;
            var server = ResolveServer(name);
            _active.SetActiveCharacter(name, server);
        }
    }

    /// <summary>Best-effort: prefer an existing snapshot's server, else the persisted server.</summary>
    private string ResolveServer(string name)
    {
        var match = _active.Characters.FirstOrDefault(c =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match.Server;
        return _active.ActiveServer ?? "";
    }
}
