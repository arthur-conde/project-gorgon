using Mithril.Shared.Audio;
using Mithril.WorldSim;
using Mithril.WorldSim.Player;

namespace Gandalf.Tests;

/// <summary>
/// Minimal <see cref="IPlayerWorld"/> stub for Gandalf scheduler-collapse
/// tests (#613). Exposes a mutable <see cref="MutableWorldClock"/> for Mode
/// + Now manipulation and a per-instance <see cref="TestEventBus"/> that
/// supports synchronous <see cref="IWorldEventBus.Subscribe{T}"/> +
/// <see cref="TestEventBus.Publish{T}"/> without the producer / folder /
/// composer dispatch machinery — tests drive synthetic frames directly.
///
/// <para>The folder / producer / composer registration surfaces throw to
/// flag accidental use; tests that need a real merger live in
/// <c>Mithril.WorldSim.Player.Tests</c> instead and reach the internal
/// <c>WorldClock</c> / <c>WorldEventBus</c> via <c>InternalsVisibleTo</c>.</para>
/// </summary>
internal sealed class TestPlayerWorld : IPlayerWorld
{
    public MutableWorldClock Clock { get; } = new();
    public TestEventBus TestBus { get; } = new();

    IWorldClock IWorld.Clock => Clock;
    IWorldEventBus IWorld.Bus => TestBus;

    public void RegisterProducer<T>(IFrameProducer<T> producer) =>
        throw new NotSupportedException("TestPlayerWorld: register on a real world.");
    public void RegisterFolder<T>(IFolder<T> folder) =>
        throw new NotSupportedException("TestPlayerWorld: register on a real world.");
    public void RegisterComposer(IComposer composer) =>
        throw new NotSupportedException("TestPlayerWorld: register on a real world.");
    public Task StartMerger(CancellationToken ct) => Task.CompletedTask;
}

internal sealed class MutableWorldClock : IWorldClock
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.MinValue;
    public long Frame { get; set; }
    public WorldMode Mode { get; set; } = WorldMode.Live;
}

/// <summary>
/// Headless typed pub-sub bus for tests — mirrors the real
/// <c>WorldEventBus</c> shape (subscribers see <see cref="Frame{T}"/>
/// payloads on the publishing thread) without the per-type registration
/// machinery. The test calls <see cref="Publish{T}(DateTimeOffset, T)"/>
/// with a payload and the bus wraps it into a <see cref="Frame{T}"/>.
/// </summary>
internal sealed class TestEventBus : IWorldEventBus
{
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();

    public IDisposable Subscribe<T>(Action<Frame<T>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!_handlers.TryGetValue(typeof(T), out var list))
        {
            _handlers[typeof(T)] = list = new List<Delegate>();
        }
        list.Add(handler);
        return new Disposable(() => list.Remove(handler));
    }

    public void Publish<T>(DateTimeOffset timestamp, T payload)
    {
        if (!_handlers.TryGetValue(typeof(T), out var list)) return;
        var frame = new Frame<T>(timestamp, payload);
        foreach (var handler in list.ToArray())
        {
            ((Action<Frame<T>>)handler)(frame);
        }
    }

    private sealed class Disposable : IDisposable
    {
        private Action? _onDispose;
        public Disposable(Action onDispose) => _onDispose = onDispose;
        public void Dispose()
        {
            _onDispose?.Invoke();
            _onDispose = null;
        }
    }
}

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
