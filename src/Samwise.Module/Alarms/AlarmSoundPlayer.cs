using System.IO;
using System.Media;
using System.Windows.Media;

namespace Samwise.Alarms;

/// <summary>
/// Plays alarm sounds from arbitrary audio files. Delegates to WPF's
/// <see cref="MediaPlayer"/> for non-WAV formats (MP3/WMA/AAC via Windows
/// Media Foundation) and keeps a lightweight <see cref="SoundPlayer"/>
/// fall-back for WAV so we don't spin up a media pipeline for the most
/// common case. Silently falls back to <see cref="SystemSounds.Asterisk"/>
/// if the file is missing or the codec isn't installed.
/// </summary>
public static class AlarmSoundPlayer
{
    // Hold the MediaPlayer alive past the Play() call; disposing it immediately
    // stops playback. One instance per call is fine — .NET's finalizer handles it.
    private static MediaPlayer? _lastPlayer;

    public static IReadOnlyList<string> SupportedExtensions { get; } = new[]
    {
        ".wav", ".mp3", ".wma", ".aac", ".m4a", ".flac", ".ogg",
    };

    public static string OpenFileFilter =>
        "Audio files|*.wav;*.mp3;*.wma;*.aac;*.m4a;*.flac;*.ogg|"
        + "WAV|*.wav|MP3|*.mp3|All files|*.*";

    public static void Play(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                if (IsWav(path))
                {
                    using var p = new SoundPlayer(path);
                    p.Play();
                    return;
                }
                var mp = new MediaPlayer();
                mp.Open(new Uri(path, UriKind.Absolute));
                mp.MediaFailed += (_, __) => FallbackBeep();
                mp.Play();
                _lastPlayer = mp;
                return;
            }
        }
        catch { /* fall through */ }
        FallbackBeep();
    }

    private static bool IsWav(string path) =>
        string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase);

    private static void FallbackBeep()
    {
        try { SystemSounds.Asterisk.Play(); } catch { }
    }
}
