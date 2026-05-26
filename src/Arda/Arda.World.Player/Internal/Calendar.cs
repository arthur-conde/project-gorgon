using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Observes every line's timestamp to emit <see cref="CalendarTimeAdvanced"/>
/// (once per wall-clock second) and <see cref="TimeOfDayShifted"/> (when the
/// in-game time-of-day crosses a shift boundary).
/// </summary>
internal sealed class Calendar : ILineObserver, ICalendarState
{
    private readonly IDomainEventPublisher _bus;
    private readonly Func<DateTimeOffset, int> _projectToGameHour;
    private readonly IReadOnlyList<(string Slug, int StartHour)> _shifts;
    private long _lastSecondTicks;
    private string? _lastShiftSlug;

    public DateTimeOffset? LastTimestamp { get; private set; }
    public string? CurrentShift => _lastShiftSlug;

    /// <param name="bus">Domain event bus for publishing.</param>
    /// <param name="projectToGameHour">
    /// Pure function projecting a wall-clock instant to PG in-game hour (0-23).
    /// In production, wired to <c>at => GameClock.Project(at).Hour</c>.
    /// </param>
    /// <param name="shifts">
    /// Shift definitions in StartHour order. Each entry is (slug, startHour).
    /// In production, sourced from <c>IShiftCatalog.Shifts</c>.
    /// </param>
    public Calendar(
        IDomainEventPublisher bus,
        Func<DateTimeOffset, int> projectToGameHour,
        IReadOnlyList<(string Slug, int StartHour)> shifts)
    {
        _bus = bus;
        _projectToGameHour = projectToGameHour;
        _shifts = shifts;
    }

    public void Observe(string log, LogLineMetadata metadata)
    {
        if (metadata.Timestamp is not { } ts)
            return;

        var secondTicks = ts.UtcTicks / TimeSpan.TicksPerSecond;
        if (secondTicks == _lastSecondTicks && LastTimestamp is not null)
            return;

        _lastSecondTicks = secondTicks;
        LastTimestamp = ts;

        _bus.Publish(new CalendarTimeAdvanced(ts, metadata));

        var gameHour = _projectToGameHour(ts);
        var slug = ResolveShift(gameHour);
        if (slug is null)
            return;

        if (string.Equals(_lastShiftSlug, slug, StringComparison.Ordinal))
            return;

        var prior = _lastShiftSlug;
        _lastShiftSlug = slug;
        _bus.Publish(new TimeOfDayShifted(prior, slug, ts, metadata));
    }

    private string? ResolveShift(int gameHour)
    {
        if (_shifts.Count == 0) return null;

        string? active = null;
        foreach (var (slug, startHour) in _shifts)
        {
            if (startHour <= gameHour) active = slug;
        }
        return active ?? _shifts[^1].Slug;
    }
}
