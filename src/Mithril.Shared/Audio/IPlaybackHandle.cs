namespace Mithril.Shared.Audio;

public interface IPlaybackHandle : IDisposable
{
    void Stop();
    bool IsPlaying { get; }
}
