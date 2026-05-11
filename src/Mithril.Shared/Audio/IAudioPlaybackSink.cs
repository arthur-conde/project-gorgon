namespace Mithril.Shared.Audio;

/// <summary>
/// DI seam over the static <see cref="AudioPlayer"/>. Production registers
/// <see cref="StaticAudioPlayerSink"/> which forwards; tests inject a
/// recording fake so AlarmService becomes unit-testable without WPF or NAudio.
/// </summary>
public interface IAudioPlaybackSink
{
    IPlaybackHandle Play(string? path, float volume = 1.0f, string? callerId = null, bool loop = false);
    void Stop();
    void Stop(string callerId);
}
