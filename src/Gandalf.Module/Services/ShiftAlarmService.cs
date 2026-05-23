using System.Windows;
using System.Windows.Threading;
using Gandalf.Domain;
using Microsoft.Extensions.Hosting;
using Mithril.Shared.Audio;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Game;
using Mithril.Shared.Wpf;
using Mithril.WorldSim;
using Mithril.WorldSim.Player;

namespace Gandalf.Services;

/// <summary>
/// Fires alarms at Project Gorgon's published in-game-time-of-day shift
/// transitions (Midnight / Dawn / Morning / Afternoon / Dusk / Night;
/// see <see cref="IShiftCatalog"/>). Distinct from the user-curated timer
/// pipeline (<see cref="TimerAlarmService"/>) — shifts are global,
/// character-agnostic, and have no Start/Done lifecycle.
///
/// <para><b>Event-driven (scheduler-collapse, #613).</b> Subscribes to
/// PlayerWorld's <see cref="TimeOfDayShift"/> domain events on
/// <see cref="StartAsync"/>; the world clock drives the transition cadence,
/// retiring the legacy <see cref="DispatcherTimer"/> wake injection (design
/// notebook §Migration item #12, principle 13). The composer
/// (<c>TimeOfDayShiftComposer</c> in <c>Mithril.WorldSim.Player</c>)
/// dedups within a bucket, so this service sees at most one event per real
/// transition.</para>
///
/// <para><b>Mode-gate at the side-effect boundary.</b> Per principle 12 +
/// PR #705 / #708, the audio playback + window flash branch gates on
/// <see cref="IWorldClock.Mode"/> == <see cref="WorldMode.Replaying"/> and
/// returns immediately — drain-time replay updates the per-shift
/// last-fired ledger but does not ring. State derivation upstream of the
/// gate stays mode-agnostic so the next Live tick reuses a coherent
/// suppression contract.</para>
/// </summary>
public sealed class ShiftAlarmService : BackgroundService
{
    private readonly IShiftCatalog _catalog;
    private readonly GandalfSettings _globalSettings;
    private readonly GandalfShiftSettings _shiftSettings;
    private readonly IAudioPlaybackSink _audio;
    private readonly IPlayerWorld _world;
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
        IPlayerWorld world,
        IDiagnosticsSink? diag = null)
    {
        _catalog = catalog;
        _globalSettings = globalSettings;
        _shiftSettings = shiftSettings;
        _audio = audio;
        _world = world;
        _diag = diag;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Subscribe synchronously before base.StartAsync — same shape as the
        // six PR #705 ingestion services. The trailing-registered merger
        // (#702 / Call 2) starts only after every hosted-service StartAsync
        // completes, so no TimeOfDayShift event slips past during cold-start.
        _subscription = _world.Bus.Subscribe<TimeOfDayShift>(frame => OnShiftTransition(frame.Payload));
        _diag?.Info("Gandalf.ShiftAlarm",
            "Subscribed to PlayerWorld TimeOfDayShift (scheduler-collapse, #613)");
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
    /// Test hook — feed a synthetic <see cref="TimeOfDayShift"/> through the
    /// transition handler without driving the world's bus.
    /// </summary>
    internal void OnShiftTransitionForTests(TimeOfDayShift shift) => OnShiftTransition(shift);

    private void OnShiftTransition(TimeOfDayShift shift)
    {
        if (_disposed) return;

        var slug = shift.To;
        // Bucket-level dedup. The composer already dedups within a bucket,
        // but a Replaying-mode tick on cold-start can emit a first
        // transition that the service has already observed on a prior run
        // (the persisted GandalfShiftSettings has no per-slug last-fired
        // mark); the slug ledger absorbs that without ringing again.
        if (string.Equals(_lastFiredSlug, slug, StringComparison.Ordinal)) return;

        ShiftDefinition? def = null;
        foreach (var s in _catalog.Shifts)
        {
            if (string.Equals(s.Slug, slug, StringComparison.Ordinal)) { def = s; break; }
        }
        if (def is null)
        {
            _diag?.Warn("Gandalf.ShiftAlarm",
                $"Received TimeOfDayShift slug='{slug}' not in catalog; ignoring");
            return;
        }

        var config = _shiftSettings.GetOrCreate(def.Slug);
        _lastFiredSlug = slug;

        if (!ShouldAlarm(config, _globalSettings)) return;

        // Call 3 / principle 12 + #708 constraint — mode-gate the user-
        // facing projection (audio playback + window flash). State
        // derivation upstream (the _lastFiredSlug write above) stays mode-
        // agnostic so the next Live tick reuses a coherent suppression
        // contract. Reference impls: Samwise.AlarmService.Fire and
        // Gandalf.TimerAlarmService.OnTimerReady (both PR #705).
        if (_world.Clock.Mode == WorldMode.Replaying) return;

        // #712 — cold-start suppression. The composer reports From == null
        // exactly once per session: the first TimeOfDayShift emission after
        // Mithril starts, carrying the in-progress shift the user already
        // sees on-screen. Pre-#709 Reschedule-based scheduling armed only
        // the NEXT transition, so cold-start was always silent; default-off
        // matches that prior behaviour. Same shape as the Mode gate above —
        // ledger has already advanced, only the audio + flash side-effect
        // is suppressed.
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
