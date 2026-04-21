using System.IO;
using System.Media;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Samwise.Alarms;

public interface IPlaybackHandle : IDisposable
{
    void Stop();
    bool IsPlaying { get; }
}

public static class AlarmSoundPlayer
{
    private static readonly object _lock = new();
    private static readonly List<PlaybackHandle> _active = new();

    static AlarmSoundPlayer()
    {
        MediaFoundationApi.Startup();
    }

    /// <summary>
    /// When true, <see cref="Play"/> does not stop existing playback first.
    /// </summary>
    public static bool ConcurrentPlayback { get; set; }

    public static IReadOnlyList<string> SupportedExtensions { get; } = new[]
    {
        ".wav", ".mp3", ".wma", ".aac", ".m4a", ".flac", ".ogg",
    };

    public static string OpenFileFilter =>
        "Audio files|*.wav;*.mp3;*.wma;*.aac;*.m4a;*.flac;*.ogg|"
        + "WAV|*.wav|MP3|*.mp3|All files|*.*";

    public static IPlaybackHandle Play(string? path, float volume = 1.0f, string? callerId = null)
    {
        try
        {
            if (!ConcurrentPlayback)
                Stop();

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                FallbackBeep();
                return PlaybackHandle.Empty;
            }

            var reader = OpenReader(path);
            var sampleProvider = reader.ToSampleProvider();
            var volumeProvider = new VolumeSampleProvider(sampleProvider)
            {
                Volume = Math.Clamp(volume, 0f, 2.0f),
            };

            var output = new WaveOutEvent();
            output.Init(volumeProvider);

            var handle = new PlaybackHandle(output, reader, callerId);

            lock (_lock)
            {
                _active.Add(handle);
            }

            output.PlaybackStopped += OnPlaybackStopped;
            output.Play();

            return handle;
        }
        catch
        {
            FallbackBeep();
            return PlaybackHandle.Empty;
        }
    }

    /// <summary>Stop all active playback (global kill).</summary>
    public static void Stop()
    {
        List<PlaybackHandle> snapshot;
        lock (_lock)
        {
            snapshot = new List<PlaybackHandle>(_active);
            _active.Clear();
        }

        foreach (var h in snapshot)
            h.DisposeInternal();
    }

    /// <summary>Stop all active playback tagged with <paramref name="callerId"/> (per-module stop).</summary>
    public static void Stop(string callerId)
    {
        List<PlaybackHandle> toStop;
        lock (_lock)
        {
            toStop = _active.Where(h => string.Equals(h.CallerId, callerId, StringComparison.Ordinal)).ToList();
            foreach (var h in toStop)
                _active.Remove(h);
        }

        foreach (var h in toStop)
            h.DisposeInternal();
    }

    private static void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (sender is not WaveOutEvent finished) return;

        PlaybackHandle? handle = null;
        lock (_lock)
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                if (_active[i].Output == finished)
                {
                    handle = _active[i];
                    _active.RemoveAt(i);
                    break;
                }
            }
        }

        handle?.DisposeInternal();
    }

    private static WaveStream OpenReader(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".mp3", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".aiff", StringComparison.OrdinalIgnoreCase))
        {
            return new AudioFileReader(path);
        }

        return new MediaFoundationReader(path);
    }

    private static void FallbackBeep()
    {
        try { SystemSounds.Asterisk.Play(); } catch { }
    }

    private sealed class PlaybackHandle : IPlaybackHandle
    {
        public static readonly PlaybackHandle Empty = new(null!, null!, null) { _disposed = true };

        internal readonly WaveOutEvent? Output;
        private readonly WaveStream? _reader;
        private bool _disposed;

        public string? CallerId { get; }

        public PlaybackHandle(WaveOutEvent? output, WaveStream? reader, string? callerId)
        {
            Output = output;
            _reader = reader;
            CallerId = callerId;
        }

        public bool IsPlaying => !_disposed && Output?.PlaybackState == PlaybackState.Playing;

        /// <summary>Per-instance stop: stops this one playback and removes from the active list.</summary>
        public void Stop()
        {
            lock (_lock)
            {
                _active.Remove(this);
            }

            DisposeInternal();
        }

        internal void DisposeInternal()
        {
            if (_disposed) return;
            _disposed = true;

            if (Output is not null)
            {
                try { Output.PlaybackStopped -= OnPlaybackStopped; } catch { }
                try { Output.Stop(); } catch { }
                try { Output.Dispose(); } catch { }
            }

            if (_reader is not null)
            {
                try { _reader.Dispose(); } catch { }
            }
        }

        public void Dispose() => Stop();
    }
}
