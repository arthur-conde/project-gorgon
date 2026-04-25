using System.Windows;
using Arwen.Domain;
using Arwen.Parsing;
using Mithril.Shared.Character;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using Mithril.Shared.Settings;
using Microsoft.Extensions.Hosting;

namespace Arwen.State;

public sealed class FavorIngestionService : BackgroundService
{
    private readonly IPlayerLogStream _stream;
    private readonly FavorLogParser _parser;
    private readonly FavorStateService _state;
    private readonly CalibrationService _calibration;
    private readonly PerCharacterView<ArwenFavorState> _favorView;
    private readonly IActiveCharacterService _activeChar;
    private readonly IDiagnosticsSink? _diag;
    private readonly ModuleGate _gate;

    public FavorIngestionService(
        IPlayerLogStream stream,
        FavorLogParser parser,
        FavorStateService state,
        CalibrationService calibration,
        PerCharacterView<ArwenFavorState> favorView,
        IActiveCharacterService activeChar,
        SettingsAutoSaver<ArwenSettings> autoSaver,
        ModuleGates gates,
        IDiagnosticsSink? diag = null)
    {
        _stream = stream;
        _parser = parser;
        _state = state;
        _calibration = calibration;
        _favorView = favorView;
        _activeChar = activeChar;
        _diag = diag;
        _ = autoSaver; // keep alive for PropertyChanged subscription
        _gate = gates.For("arwen");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _diag?.Info("Arwen", "Waiting for module gate…");
        await _gate.WaitAsync(stoppingToken).ConfigureAwait(false);
        _diag?.Info("Arwen", "Gate opened — subscribing to Player.log for favor events");

        await foreach (var raw in _stream.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            var evt = _parser.TryParse(raw.Line, raw.Timestamp);
            if (evt is null) continue;

            switch (evt)
            {
                case FavorUpdate update:
                    _diag?.Trace("Arwen.Parse", $"FavorUpdate npc={update.NpcKey} favor={update.AbsoluteFavor:F1}");
                    _calibration.OnStartInteraction(update.NpcKey);
                    Dispatch(() =>
                    {
                        var favor = _favorView.Current;
                        if (favor is null) return;
                        favor.SetExactFavor(update.NpcKey, update.AbsoluteFavor, DateTimeOffset.UtcNow);
                        _favorView.Save();
                        _state.OnFavorUpdated(update.NpcKey);
                    });
                    break;

                case ItemDeleted deleted:
                    _calibration.OnItemDeleted(deleted.InstanceId);
                    break;

                case FavorDelta delta:
                    _diag?.Trace("Arwen.Parse", $"FavorDelta npc={delta.NpcKey} delta={delta.Delta:F1}");
                    _calibration.OnDeltaFavor(delta.NpcKey, delta.Delta);
                    break;
            }
        }
    }

    private static void Dispatch(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.InvokeAsync(a);
    }
}
