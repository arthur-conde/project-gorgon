using System.Windows;
using Gorgon.Shared.Character;
using Gorgon.Shared.Diagnostics;
using Gorgon.Shared.Logging;
using Gorgon.Shared.Modules;
using Microsoft.Extensions.Hosting;
using Pippin.Domain;
using Pippin.Parsing;

namespace Pippin.State;

public sealed class GourmandIngestionService : BackgroundService
{
    private readonly IPlayerLogStream _stream;
    private readonly GourmandLogParser _parser;
    private readonly GourmandStateMachine _state;
    private readonly GourmandStateService _stateService;
    private readonly PerCharacterView<GourmandState> _view;
    private readonly IDiagnosticsSink? _diag;
    private readonly ModuleGate _gate;

    public GourmandIngestionService(
        IPlayerLogStream stream,
        GourmandLogParser parser,
        GourmandStateMachine state,
        GourmandStateService stateService,
        PerCharacterView<GourmandState> view,
        ModuleGates gates,
        IDiagnosticsSink? diag = null)
    {
        _stream = stream;
        _parser = parser;
        _state = state;
        _stateService = stateService;
        _view = view;
        _diag = diag;
        _gate = gates.For("pippin");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _diag?.Info("Pippin", "Waiting for module gate…");
        await _gate.WaitAsync(stoppingToken).ConfigureAwait(false);
        _diag?.Info("Pippin", "Gate opened — waiting for active character…");

        await WaitForActiveCharacterAsync(stoppingToken).ConfigureAwait(false);

        try { await _stateService.LoadAsync(stoppingToken).ConfigureAwait(false); }
        catch (Exception ex) { _diag?.Warn("Pippin", $"Failed to load state: {ex.Message}"); }

        _diag?.Info("Pippin", "State hydrated — subscribing to Player.log");

        await foreach (var raw in _stream.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            var evt = _parser.TryParse(raw.Line, raw.Timestamp);
            if (evt is GourmandEvent ge)
            {
                _diag?.Trace("Pippin.Parse", $"FoodsConsumedReport with {(ge as FoodsConsumedReport)?.Foods.Count ?? 0} entries");
                Dispatch(() => _state.Apply(ge));
            }
        }
    }

    private async Task WaitForActiveCharacterAsync(CancellationToken ct)
    {
        if (_view.Current is not null) return;
        var tcs = new TaskCompletionSource();
        void Handler(object? _, EventArgs __)
        {
            if (_view.Current is not null) tcs.TrySetResult();
        }
        _view.CurrentChanged += Handler;
        try
        {
            if (_view.Current is not null) return;
            using (ct.Register(() => tcs.TrySetCanceled(ct)))
            {
                await tcs.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            _view.CurrentChanged -= Handler;
        }
    }

    private static void Dispatch(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.InvokeAsync(a);
    }
}
