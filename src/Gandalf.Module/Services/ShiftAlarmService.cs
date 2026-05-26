using System.Windows;
using System.Windows.Threading;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Gandalf.Domain;
using Microsoft.Extensions.Hosting;
using Mithril.Shared.Audio;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Game;
using Mithril.Shared.Wpf;

namespace Gandalf.Services;

/// <summary>
/// Fires alarms at Project Gorgon's published in-game-time-of-day shift
/// transitions (Midnight / Dawn / Morning / Afternoon / Dusk / Night;
/// see <see cref="IShiftCatalog"/>). Distinct from the user-curated timer
/// pipeline (<see cref="TimerAlarmService"/>) — shifts are global,
/// character-agnostic, and have no Start/Done lifecycle.
///
/// <para><b>Event-driven (Arda migration).</b> Subscribes to
/// <see cref="TimeOfDayShifted"/> domain events via
/// <see cref="IDomainEventSubscriber"/>. The Arda pipeline drives the
/// transition cadence from the log stream.</para>
///
/// <para><b>Mode-gate at the side-effect boundary.</b>
/// The audio playback + window flash branch gates on
/// <see cref="TimeOfDayShifted.Metadata"/>.<see cref="Arda.Abstractions.Logs.LogLineMetadata.IsReplay"/>
/// and returns immediately — replay updates the per-shift last-fired
/// ledger but does not ring.</para>
/// </summary>
public sealed class ShiftAlarmService : BackgroundService
{
    private readonly IShiftCatalog _catalog;
    private readonly GandalfSettings _globalSettings;
    private readonly GandalfShiftSettings _shiftSettings;
    private readonly IAudioPlaybackSink _audio;
    private readonly IDomainEventSubscriber _bus;
    private readonly IDiagnosticsSink? _diag;
    private readonly Dictionary<string, IPlaybackHandle> _playback = new(StringComparer.Ordinal);
    private IDisposable? _subscription;
    private string? _lastFiredSlug;
    private bool _disposed;

    public ShiftAlarmService(
        IShiftCatalog catalog,
        GandalfSettings globalSettings,
        GandalfShiftSettings shiftSettings,
        IAudioPlaybackSink audio,
        IDomainEventSubscriber bus,
        IDiagnosticsSink? diag = null)
    {
        _catalog = catalog;
        _globalSettings = globalSettings;
        _shiftSettings = shiftSettings;
        _audio = audio;
        _bus = bus;
        _diag = diag;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _bus.Subscribe<TimeOfDayShifted>(OnShiftTransition);
        _diag?.Info("Gandalf.ShiftAlarm",
            "Subscribed to Arda TimeOfDayShifted events");
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* expected on host stop */ }
        finally
        {
            _subscription?.Dispose();
            _subscription = null;
        }
    }

    /// <summary>
    /// The slug of the most recent shift the service applied — surfaced for
    /// diagnostics + tests. Production code reads
    /// <see cref="GandalfShiftSettings"/> directly.
    /// </summary>
    public string? LastObservedShift => _lastFiredSlug;

    /// <summary>
    /// Stop any in-flight alarm playback for this shift (and any others) and
    /// reset state. Symmetric with <see cref="TimerAlarmService.DismissAll"/>.
    /// </summary>
    public void DismissAll()
    {
        foreach (var h in _playback.Values) h.Stop();
        _playback.Clear();
    }

    /// <summary>
    /// Decision logic for "should this shift transition fire an alarm right
    /// now?" — extracted as a static so unit tests can exercise it without
    /// driving the dispatcher pump or invoking <c>AudioPlayer.Play</c>.
    /// </summary>
    internal static bool ShouldAlarm(ShiftAlarmConfig config, GandalfSettings global) =>
        global.AlarmEnabled && config.Enabled;

    /// <summary>
    /// Per-shift <see cref="ShiftAlarmConfig.SoundFilePath"/> wins; null falls
    /// back to the global <see cref="GandalfSettings.SoundFilePath"/>. Mirrors
    /// <see cref="TimerAlarmService.ResolveSoundPath"/> so the per-row /
    /// per-shift override behavior is identical.
    /// </summary>
    internal static string? ResolveSoundPath(ShiftAlarmConfig config, GandalfSettings global) =>
        config.SoundFilePath ?? global.SoundFilePath;

    /// <summary>
    /// Test hook — feed a synthetic <see cref="TimeOfDayShifted"/> through the
    /// transition handler without driving the bus.
    /// </summary>
    internal void OnShiftTransitionForTests(TimeOfDayShifted shift) => OnShiftTransition(shift);

    private void OnShiftTransition(TimeOfDayShifted shift)
    {
        if (_disposed) return;

        var slug = shift.To;
        if (string.Equals(_lastFiredSlug, slug, StringComparison.Ordinal)) return;

        ShiftDefinition? def = null;
        foreach (var s in _catalog.Shifts)
        {
            if (string.Equals(s.Slug, slug, StringComparison.Ordinal)) { def = s; break; }
        }
        if (def is null)
        {
            _diag?.Warn("Gandalf.ShiftAlarm",
                $"Received TimeOfDayShifted slug='{slug}' not in catalog; ignoring");
            return;
        }

        var config = _shiftSettings.GetOrCreate(def.Slug);
        _lastFiredSlug = slug;

        if (!ShouldAlarm(config, _globalSettings)) return;

        // Mode-gate: suppress audio during replay.
        if (shift.Metadata.IsReplay) return;

        // Cold-start suppression (#712): From == null means the first emission
        // after Mithril starts, carrying the in-progress shift. Default-off.
        if (shift.From is null && !_shiftSettings.RingOnCurrentShiftAtStartup) return;

        var path = ResolveSoundPath(config, _globalSettings);
        Dispatch(() =>
        {
            if (_playback.Remove(def.Slug, out var prior)) prior.Stop();
            _playback[def.Slug] = _audio.Play(
                path, (float)_globalSettings.AlarmVolume, "gandalf");
            if (_globalSettings.FlashWindow)
            {
                var win = Application.Current?.MainWindow;
                if (win is not null) WindowFlasher.Flash(win);
            }
        });
    }

    private static void Dispatch(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.BeginInvoke(action);
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _subscription?.Dispose();
        DismissAll();
        base.Dispose();
    }
}
