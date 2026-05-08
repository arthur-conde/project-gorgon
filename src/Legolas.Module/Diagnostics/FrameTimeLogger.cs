using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace Legolas.Diagnostics;

/// <summary>
/// Captures wall-clock frame intervals by hooking <see cref="CompositionTarget.Rendering"/>
/// on the WPF UI thread. Each fired Rendering pass implies one composition pass; the
/// delta between successive fires is what the user actually sees as smoothness.
///
/// Singleton. Start/Stop are idempotent and safe to call from any thread (event hook
/// itself runs on the dispatcher). On stop, writes a per-run CSV (one row per frame)
/// plus a summary text file with percentiles + the config snapshot the run was made
/// against, so two runs can be compared without remembering which knobs were on.
/// </summary>
public sealed class FrameTimeLogger
{
    private readonly object _lock = new();
    private readonly string _outputDir;

    private bool _running;
    private string _label = "";
    private DateTime _startedAt;
    private Stopwatch? _stopwatch;
    private double _lastSampleMs;
    private List<double> _samples = new();
    private FrameRunConfig _config = FrameRunConfig.Empty;

    public FrameTimeLogger(string outputDir)
    {
        _outputDir = outputDir;
        Directory.CreateDirectory(_outputDir);
    }

    public bool IsRunning { get { lock (_lock) return _running; } }

    public string OutputDirectory => _outputDir;

    public void Start(string label, FrameRunConfig config)
    {
        lock (_lock)
        {
            // Take over if a previous run is still active. Earlier behaviour
            // was silent no-op, which let the manual logger silently eat the
            // harness's listening-phase capture and write the result under
            // the wrong label/config. Stop() flushes the in-progress report
            // to disk so the takeover is visible, not destructive.
            if (_running) Stop();
            _running = true;
            _label = string.IsNullOrWhiteSpace(label) ? "run" : label;
            _config = config;
            _startedAt = DateTime.Now;
            _stopwatch = Stopwatch.StartNew();
            _lastSampleMs = 0;
            _samples = new List<double>(capacity: 4096);
        }

        // Subscribe on the UI dispatcher. CompositionTarget.Rendering binds
        // its handler list to Dispatcher.CurrentDispatcher; called from a
        // threadpool thread that creates a fresh dispatcher whose
        // CompositionTarget never fires, silently producing zero samples.
        // Hooking outside the lock so we don't block the dispatcher waiting
        // on a lock the UI thread doesn't hold.
        InvokeOnUi(() => CompositionTarget.Rendering += OnRendering);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // Inside the lock would serialize every render frame against Stop(); cheaper
        // to read the running flag racily and accept that one stale sample may sneak
        // in around Stop(). The Stop() report uses Skip(1) on the head and trims
        // outliers anyway.
        if (!_running || _stopwatch is null) return;
        var nowMs = _stopwatch.Elapsed.TotalMilliseconds;
        var dt = nowMs - _lastSampleMs;
        _lastSampleMs = nowMs;
        // Drop the very first delta (it's "time since Start" not a frame interval),
        // and outliers from things like the debugger pausing.
        if (_samples.Count == 0) { _samples.Add(0); return; }
        if (dt > 0 && dt < 5000) _samples.Add(dt);
    }

    public FrameTimeReport? Stop()
    {
        // Unhook on the UI dispatcher to mirror Start's subscription path —
        // otherwise the remove targets the wrong media context and leaks the
        // handler.
        InvokeOnUi(() => CompositionTarget.Rendering -= OnRendering);

        lock (_lock)
        {
            if (!_running) return null;
            _stopwatch?.Stop();
            _running = false;

            // First entry is a sentinel from OnRendering; real samples start at index 1.
            var samples = _samples.Count > 1 ? _samples.GetRange(1, _samples.Count - 1).ToArray() : Array.Empty<double>();
            var report = FrameTimeReport.From(_label, _startedAt, _config, samples);
            try { WriteReport(report); } catch { /* best-effort; don't crash the harness */ }
            return report;
        }
    }

    private static void InvokeOnUi(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) { action(); return; }
        if (d.CheckAccess()) action();
        else d.Invoke(action);
    }

    private void WriteReport(FrameTimeReport report)
    {
        var stamp = report.StartedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var safeLabel = string.Concat(report.Label.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        var csvPath = Path.Combine(_outputDir, $"{stamp}-{safeLabel}.csv");
        var txtPath = Path.Combine(_outputDir, $"{stamp}-{safeLabel}.txt");

        using (var w = new StreamWriter(csvPath, append: false, Encoding.UTF8))
        {
            w.WriteLine("frame_index,dt_ms");
            for (var i = 0; i < report.FrameTimesMs.Count; i++)
            {
                w.Write(i.ToString(CultureInfo.InvariantCulture));
                w.Write(',');
                w.WriteLine(report.FrameTimesMs[i].ToString("F3", CultureInfo.InvariantCulture));
            }
        }

        using (var w = new StreamWriter(txtPath, append: false, Encoding.UTF8))
        {
            w.WriteLine($"# Legolas frame-time report");
            w.WriteLine($"# label:           {report.Label}");
            w.WriteLine($"# started:         {report.StartedAt:O}");
            w.WriteLine($"# duration_s:      {report.DurationSeconds.ToString("F2", CultureInfo.InvariantCulture)}");
            w.WriteLine($"# frames:          {report.FrameTimesMs.Count}");
            w.WriteLine($"# fps_mean:        {report.MeanFps.ToString("F2", CultureInfo.InvariantCulture)}");
            w.WriteLine($"# dt_ms_mean:      {report.MeanMs.ToString("F2", CultureInfo.InvariantCulture)}");
            w.WriteLine($"# dt_ms_p50:       {report.P50Ms.ToString("F2", CultureInfo.InvariantCulture)}");
            w.WriteLine($"# dt_ms_p95:       {report.P95Ms.ToString("F2", CultureInfo.InvariantCulture)}");
            w.WriteLine($"# dt_ms_p99:       {report.P99Ms.ToString("F2", CultureInfo.InvariantCulture)}");
            w.WriteLine($"# dt_ms_max:       {report.MaxMs.ToString("F2", CultureInfo.InvariantCulture)}");
            w.WriteLine($"# stutter_>33ms:   {report.StutterCount}");
            w.WriteLine($"#");
            w.WriteLine($"# Config snapshot:");
            w.WriteLine($"#   pin_count:               {report.Config.PinCount}");
            w.WriteLine($"#   active_treatment:        {report.Config.ActiveTreatment}");
            w.WriteLine($"#   allows_transparency:     {report.Config.AllowsTransparency}");
            w.WriteLine($"#   click_through_map:       {report.Config.ClickThroughMap}");
            w.WriteLine($"#   show_bearing_wedges:     {report.Config.ShowBearingWedges}");
            w.WriteLine($"#   show_route_lines:        {report.Config.ShowRouteLines}");
            w.WriteLine($"#   map_window_size:         {report.Config.MapWidth}x{report.Config.MapHeight}");
            w.WriteLine($"#   fsm_state:               {report.Config.FsmState}");
        }
    }
}

public sealed record FrameRunConfig(
    int PinCount,
    string ActiveTreatment,
    bool AllowsTransparency,
    bool ClickThroughMap,
    bool ShowBearingWedges,
    bool ShowRouteLines,
    double MapWidth,
    double MapHeight,
    string FsmState)
{
    public static FrameRunConfig Empty { get; } =
        new(0, "?", false, false, false, false, 0, 0, "?");
}

public sealed class FrameTimeReport
{
    public string Label { get; }
    public DateTime StartedAt { get; }
    public IReadOnlyList<double> FrameTimesMs { get; }
    public FrameRunConfig Config { get; }

    public double DurationSeconds { get; }
    public double MeanMs { get; }
    public double MeanFps { get; }
    public double P50Ms { get; }
    public double P95Ms { get; }
    public double P99Ms { get; }
    public double MaxMs { get; }
    public int StutterCount { get; }

    private FrameTimeReport(string label, DateTime startedAt, FrameRunConfig config, double[] samples)
    {
        Label = label;
        StartedAt = startedAt;
        Config = config;
        FrameTimesMs = samples;

        if (samples.Length == 0)
        {
            DurationSeconds = 0;
            MeanMs = MeanFps = P50Ms = P95Ms = P99Ms = MaxMs = 0;
            StutterCount = 0;
            return;
        }

        DurationSeconds = samples.Sum() / 1000.0;
        MeanMs = samples.Average();
        MeanFps = MeanMs > 0 ? 1000.0 / MeanMs : 0;

        var sorted = (double[])samples.Clone();
        Array.Sort(sorted);
        P50Ms = Percentile(sorted, 0.50);
        P95Ms = Percentile(sorted, 0.95);
        P99Ms = Percentile(sorted, 0.99);
        MaxMs = sorted[^1];
        // 33ms ~ 30fps; anything above is a visible stutter.
        StutterCount = samples.Count(s => s > 33.0);
    }

    public static FrameTimeReport From(string label, DateTime startedAt, FrameRunConfig config, double[] samples)
        => new(label, startedAt, config, samples);

    private static double Percentile(double[] sorted, double q)
    {
        if (sorted.Length == 0) return 0;
        var idx = (int)Math.Clamp(Math.Round(q * (sorted.Length - 1)), 0, sorted.Length - 1);
        return sorted[idx];
    }
}
