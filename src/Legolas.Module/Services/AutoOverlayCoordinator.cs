using System.ComponentModel;
using System.Windows;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.ViewModels;
using Mithril.Shared.Modules;
using Microsoft.Extensions.Hosting;

namespace Legolas.Services;

/// <summary>
/// Issue #4 bullets 2 + 4: when the inventory overlay is visible AND a survey
/// session is in progress, auto-flip <see cref="LegolasSettings.ClickThroughInventory"/>
/// so map clicks pass through to the game window. Opt out via
/// <see cref="LegolasSettings.AutoClickThroughInventoryDuringSession"/>.
/// </summary>
/// <remarks>
/// We never flip click-through OFF here. The user can still toggle it back
/// manually mid-session if they need to interact with the overlay; on the next
/// state transition we will re-enable it. This is the cheapest contract that
/// matches "automatic" without fighting the user.
/// </remarks>
public sealed class AutoOverlayCoordinator : IHostedService
{
    private readonly ModuleGates _gates;
    private readonly LegolasSettings _settings;
    private readonly SessionState _session;
    private readonly SurveyFlowController _surveyFlow;
    private readonly CancellationTokenSource _stopCts = new();
    private Task? _activationTask;
    private bool _subscribed;

    public AutoOverlayCoordinator(
        ModuleGates gates,
        LegolasSettings settings,
        SessionState session,
        SurveyFlowController surveyFlow)
    {
        _gates = gates;
        _settings = settings;
        _session = session;
        _surveyFlow = surveyFlow;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _activationTask = Task.Run(async () =>
        {
            try
            {
                await _gates.For("legolas").WaitAsync(_stopCts.Token).ConfigureAwait(false);
                if (Application.Current?.Dispatcher is { } dispatcher)
                {
                    await dispatcher.InvokeAsync(Initialize);
                }
                else
                {
                    Initialize();
                }
            }
            catch (OperationCanceledException) { }
        }, _stopCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopCts.Cancel();
        if (_activationTask is not null) { try { await _activationTask.ConfigureAwait(false); } catch { } }
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            await dispatcher.InvokeAsync(Teardown);
        }
        else
        {
            Teardown();
        }
    }

    /// <summary>
    /// Begin observing flow + session and apply the auto-click-through rule once.
    /// Public for testability — the hosted-service path calls this after the module
    /// gate opens.
    /// </summary>
    public void Initialize()
    {
        if (_subscribed) return;
        _session.PropertyChanged += OnSessionPropertyChanged;
        _surveyFlow.PropertyChanged += OnFlowPropertyChanged;
        _subscribed = true;
        Evaluate();
    }

    /// <summary>Stop observing. Public for symmetry with <see cref="Initialize"/>.</summary>
    public void Teardown()
    {
        if (!_subscribed) return;
        _session.PropertyChanged -= OnSessionPropertyChanged;
        _surveyFlow.PropertyChanged -= OnFlowPropertyChanged;
        _subscribed = false;
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionState.IsInventoryVisible))
            Evaluate();
    }

    private void OnFlowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SurveyFlowController.CurrentState))
            Evaluate();
    }

    private void Evaluate()
    {
        if (!_settings.AutoClickThroughInventoryDuringSession) return;
        if (!_session.IsInventoryVisible) return;
        if (!IsActiveSession(_surveyFlow.CurrentState)) return;
        if (_settings.ClickThroughInventory) return;
        _settings.ClickThroughInventory = true;
    }

    private static bool IsActiveSession(SurveyFlowState state) =>
        state != SurveyFlowState.AwaitingPosition;
}
