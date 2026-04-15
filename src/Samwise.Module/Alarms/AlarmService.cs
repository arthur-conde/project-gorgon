using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Samwise.State;

namespace Samwise.Alarms;

public sealed record ActiveAlarm(string Key, string CharName, string CropType, DateTimeOffset Triggered);

public sealed partial class AlarmService : IDisposable
{
    private readonly GardenStateMachine _state;
    private readonly SamwiseSettings _settings;
    private readonly Dictionary<string, DateTimeOffset> _firedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _snoozedUntil = new(StringComparer.Ordinal);

    public event EventHandler<ActiveAlarm>? AlarmTriggered;

    public AlarmService(GardenStateMachine state, SamwiseSettings settings)
    {
        _state = state;
        _settings = settings;
        _state.PlotChanged += OnPlotChanged;
    }

    public IReadOnlyCollection<string> ActiveKeys => _firedAt.Keys.ToArray();

    public void SnoozeAll()
    {
        var until = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(_settings.Alarms.SnoozeMinutes);
        foreach (var key in _firedAt.Keys.ToArray()) _snoozedUntil[key] = until;
        _firedAt.Clear();
    }

    public void DismissAll() => _firedAt.Clear();

    private void OnPlotChanged(object? sender, PlotChangedArgs e)
    {
        if (!_settings.Alarms.Enabled) return;
        if (e.Plot.CropType is null) return;
        // Hydration (oldStage is null) is a restore, not a transition — never alarm.
        if (e.OldStage is null) return;
        if (e.NewStage == e.OldStage) return;
        if (!_settings.Alarms.Rules.TryGetValue(e.NewStage, out var rule) || !rule.Enabled) return;
        if (_state.IsLikelyGarbageCollected(e.Plot)) return;
        if (_settings.Alarms.MutedCrops.Contains(e.Plot.CropType)) return;
        if (_settings.Alarms.MutedCharacters.Contains(e.Plot.CharName)) return;

        // Dedupe per (plot, stage) so a re-trigger for the same stage is ignored,
        // but a later Ripe transition after an earlier Thirsty alarm still fires.
        var key = $"{e.Plot.CharName}|{e.Plot.PlotId}|{e.NewStage}";
        if (_snoozedUntil.TryGetValue(key, out var until) && until > DateTimeOffset.UtcNow) return;
        if (_firedAt.ContainsKey(key)) return;

        _firedAt[key] = DateTimeOffset.UtcNow;
        Fire(new ActiveAlarm(key, e.Plot.CharName, e.Plot.CropType, DateTimeOffset.UtcNow), rule.SoundFilePath);
    }

    private void Fire(ActiveAlarm alarm, string? soundFilePath)
    {
        Dispatch(() =>
        {
            AlarmSoundPlayer.Play(soundFilePath);

            if (_settings.Alarms.FlashWindow)
            {
                var win = Application.Current?.MainWindow;
                if (win is not null) FlashWindow(win);
            }

            AlarmTriggered?.Invoke(this, alarm);
        });
    }

    private static void Dispatch(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.InvokeAsync(a, DispatcherPriority.Normal);
    }

    public void HandleHarvested(Plot plot)
    {
        // Strip any stage-tagged entries for this plot.
        var prefix = $"{plot.CharName}|{plot.PlotId}|";
        foreach (var k in _firedAt.Keys.Where(k => k.StartsWith(prefix)).ToArray()) _firedAt.Remove(k);
        foreach (var k in _snoozedUntil.Keys.Where(k => k.StartsWith(prefix)).ToArray()) _snoozedUntil.Remove(k);
    }

    public void Dispose() { _state.PlotChanged -= OnPlotChanged; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FLASHWINFO pwfi);

    private static void FlashWindow(Window window)
    {
        var helper = new System.Windows.Interop.WindowInteropHelper(window);
        var fi = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = helper.Handle,
            dwFlags = 0x0000000F, // FLASHW_ALL | FLASHW_TIMERNOFG = 3 | 12
            uCount = 5,
            dwTimeout = 0,
        };
        FlashWindowEx(ref fi);
    }
}
