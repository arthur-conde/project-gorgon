using Mithril.Shared.Audio;

namespace Samwise.Tests.Alarms;

internal sealed class FakeAudioPlaybackSink : IAudioPlaybackSink
{
    public sealed record PlayCall(string? Path, float Volume, string? CallerId, bool Loop, FakePlaybackHandle Handle);

    public List<PlayCall> Plays { get; } = new();
    public int GlobalStopCount { get; private set; }
    public List<string> CallerStops { get; } = new();

    public IPlaybackHandle Play(string? path, float volume = 1.0f, string? callerId = null, bool loop = false)
    {
        var handle = new FakePlaybackHandle();
        Plays.Add(new PlayCall(path, volume, callerId, loop, handle));
        return handle;
    }

    public void Stop() => GlobalStopCount++;
    public void Stop(string callerId) => CallerStops.Add(callerId);
}

internal sealed class FakePlaybackHandle : IPlaybackHandle
{
    public bool Disposed { get; private set; }
    public bool IsPlaying { get; set; } = true;
    public void Stop() { IsPlaying = false; Disposed = true; }
    public void Dispose() => Stop();
}
