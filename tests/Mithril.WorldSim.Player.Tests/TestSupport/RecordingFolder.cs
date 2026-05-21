namespace Mithril.WorldSim.Player.Tests.TestSupport;

/// <summary>
/// Folder that captures every <see cref="Frame{TPayload}"/> applied and the
/// clock snapshot at apply-time. Used to assert (a) draining order, (b)
/// clock advancement, (c) per-frame mode visible to folders.
/// </summary>
internal sealed class RecordingFolder<T> : IFolder<T>
{
    public List<AppliedFrame<T>> Applied { get; } = new();

    public IReadOnlyList<IChangeEvent> Apply(Frame<T> frame, IWorldClock clock)
    {
        Applied.Add(new AppliedFrame<T>(frame, clock.Now, clock.Frame, clock.Mode));
        return Array.Empty<IChangeEvent>();
    }
}

internal readonly record struct AppliedFrame<T>(
    Frame<T> Frame,
    DateTimeOffset ClockNow,
    long ClockFrame,
    WorldMode Mode);
