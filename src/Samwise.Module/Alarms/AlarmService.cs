using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
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
        if (e.NewStage != PlotStage.Ripe || e.OldStage == PlotStage.Ripe) return;
        if (_settings.Alarms.MutedCrops.Contains(e.Plot.CropType)) return;
        if (_settings.Alarms.MutedCharacters.Contains(e.Plot.CharName)) return;

        var key = $"{e.Plot.CharName}|{e.Plot.PlotId}";
        if (_snoozedUntil.TryGetValue(key, out var until) && until > DateTimeOffset.UtcNow) return;
        if (_firedAt.ContainsKey(key)) return;

        _firedAt[key] = DateTimeOffset.UtcNow;
        Fire(new ActiveAlarm(key, e.Plot.CharName, e.Plot.CropType, DateTimeOffset.UtcNow));
    }

    private void Fire(ActiveAlarm alarm)
    {
        Dispatch(() =>
        {
            try
            {
                if (!string.IsNullOrEmpty(_settings.Alarms.SoundFilePath) && File.Exists(_settings.Alarms.SoundFilePath))
                {
                    using var p = new SoundPlayer(_settings.Alarms.SoundFilePath);
                    p.Play();
                }
                else
                {
                    SystemSounds.Asterisk.Play();
                }
            }
            catch { }

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
        var key = $"{plot.CharName}|{plot.PlotId}";
        _firedAt.Remove(key);
        _snoozedUntil.Remove(key);
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
