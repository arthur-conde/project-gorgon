using Arda.World.Player;

namespace Gandalf.Tests;

/// <summary>
/// Mutable <see cref="ICalendarState"/> test double. Tests set
/// <see cref="LastTimestamp"/> and <see cref="CurrentShift"/> directly.
/// </summary>
internal sealed class FakeCalendarState : ICalendarState
{
    public DateTimeOffset? LastTimestamp { get; set; }
    public string? CurrentShift { get; set; }
}
