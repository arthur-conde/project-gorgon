namespace Mithril.Shared.Audio;

internal sealed class StaticAudioPlayerSink : IAudioPlaybackSink
{
    public IPlaybackHandle Play(string? path, float volume = 1.0f, string? callerId = null, bool loop = false)
        => AudioPlayer.Play(path, volume, callerId, loop);

    public void Stop() => AudioPlayer.Stop();
    public void Stop(string callerId) => AudioPlayer.Stop(callerId);
}
