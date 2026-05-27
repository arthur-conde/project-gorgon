using Microsoft.Extensions.Logging;
using System.Windows;
using System.Windows.Threading;
using Arda.Contracts;
using Mithril.Shared.Character;
using Microsoft.Extensions.Hosting;
using Pippin.Domain;
using Pippin.Parsing;
using ArdaFoodsConsumedReport = Arda.World.Player.Events.FoodsConsumedReport;

namespace Pippin.State;

/// <summary>
/// Subscribes to the Arda domain bus for <see cref="ArdaFoodsConsumedReport"/>
/// events and feeds parsed food entries into <see cref="GourmandStateMachine"/>.
///
/// <para><b>Threading.</b> The Arda bus fires synchronously on the driver
/// thread. In WPF contexts we marshal onto the dispatcher so state mutations
/// stay on the UI thread; in test/headless contexts where
/// <see cref="Application.Current"/> is null we run inline.</para>
/// </summary>
public sealed class GourmandIngestionService : BackgroundService
{
    private readonly GourmandStateMachine _state;
    private readonly GourmandStateService _stateService;
    private readonly PerCharacterView<GourmandState> _view;
    private readonly ILogger? _logger;
    private readonly IDisposable? _subscription;
    private Dispatcher? _dispatcher;

    public GourmandIngestionService(
        IDomainEventSubscriber bus,
        GourmandStateMachine state,
        GourmandStateService stateService,
        PerCharacterView<GourmandState> view,
        ILogger? logger = null)
    {
        _state = state;
        _stateService = stateService;
        _view = view;
        _logger = logger;

        _subscription = bus.Subscribe<ArdaFoodsConsumedReport>(OnFoodsConsumedReport);
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_view.Current is not null)
        {
            try { await _stateService.LoadAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Failed to load state"); }
            _logger?.LogInformation("State hydrated for active character — subscribed to Arda domain bus (FoodsConsumedReport)");
        }
        else
        {
            _logger?.LogInformation("No persisted active character — subscribed to Arda domain bus eagerly; hydrate deferred");
        }

        _dispatcher = Application.Current?.Dispatcher;

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on host stop */ }
    }

    private void OnFoodsConsumedReport(ArdaFoodsConsumedReport report)
    {
        var body = report.Body.Span.ToString();
        var foods = GourmandLogParser.ParseBody(body);
        if (foods.Count == 0) return;

        var ts = report.Metadata.Timestamp ?? report.Metadata.ReadOn;

        void Apply()
        {
            _logger?.LogTrace($"FoodsConsumedReport with {foods.Count} entries (replay={report.Metadata.IsReplay})");
            var evt = new Parsing.FoodsConsumedReport(ts.UtcDateTime, foods);
            _state.Apply(evt);
        }

        if (_dispatcher is null || _dispatcher.CheckAccess())
            Apply();
        else
            _dispatcher.InvokeAsync(Apply);
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        base.Dispose();
    }
}
