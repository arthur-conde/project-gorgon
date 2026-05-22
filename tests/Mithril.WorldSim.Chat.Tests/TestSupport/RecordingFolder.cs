namespace Mithril.WorldSim.Chat.Tests.TestSupport;

/// <summary>
/// Folder that captures every frame applied + the clock snapshot at apply
/// time. Used to assert drain order, clock advancement, and the per-frame
/// mode visible to folders during dispatch.
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
