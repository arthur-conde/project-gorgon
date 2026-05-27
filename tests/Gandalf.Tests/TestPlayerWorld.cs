using Mithril.Shared.Audio;

namespace Gandalf.Tests;

/// <summary>
/// Recording <see cref="IAudioPlaybackSink"/> for mode-gating verification.
/// Shared between <c>TimerAlarmServiceTests</c> and
/// <c>ShiftAlarmServiceTests</c>.
/// </summary>
internal sealed class RecordingAudioSink : IAudioPlaybackSink
{
    public sealed record PlayCall(string? Path, float Volume, string? CallerId, bool Loop);

    public List<PlayCall> Plays { get; } = new();
    public int GlobalStopCount { get; private set; }
    public List<string> CallerStops { get; } = new();

    public IPlaybackHandle Play(string? path, float volume = 1.0f, string? callerId = null, bool loop = false)
    {
        Plays.Add(new PlayCall(path, volume, callerId, loop));
        return EmptyHandle.Instance;
    }

    public void Stop() => GlobalStopCount++;
    public void Stop(string callerId) => CallerStops.Add(callerId);

    private sealed class EmptyHandle : IPlaybackHandle
    {
        public static readonly EmptyHandle Instance = new();
        public bool IsPlaying => false;
        public void Stop() { }
        public void Dispose() { }
    }
}
