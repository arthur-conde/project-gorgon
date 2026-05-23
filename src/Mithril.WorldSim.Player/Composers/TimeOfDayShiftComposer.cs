using Mithril.Shared.Game;

namespace Mithril.WorldSim.Player.Composers;

/// <summary>
/// Composer that emits <see cref="TimeOfDayShift"/> domain events on the
/// PlayerWorld bus whenever a <see cref="CalendarTimeAdvanced"/> tick crosses
/// the boundary between two consecutive PG shift buckets (Midnight / Dawn /
/// Morning / Afternoon / Dusk / Night per <see cref="IShiftCatalog"/>).
///
/// <para><b>Why composer-derived.</b> Shift transitions are not in the source
/// stream — they are a projection of the simulated clock onto PG's published
/// shift table (the same table the shell's "Next: Dawn in 4m 23s" chip uses).
/// Principle 10 (folders / composers / producers) puts projection on the
/// composer side so the source-stream shape stays minimal and a Gorgon patch
/// retuning the shift table reshapes exactly one composer rather than every
/// subscriber.</para>
///
/// <para><b>Emission shape.</b> One event per bucket transition observed —
/// regardless of how many calendar ticks land inside the same bucket. The
/// very first tick after a world starts emits <c>From == null</c>,
/// <c>To = current shift</c>; subsequent transitions carry the prior
/// shift's slug. Slugs come from <see cref="IShiftCatalog"/>, so persistence
/// keyed off them (Gandalf's per-shift alarm config) stays consistent across
/// catalog schema versions.</para>
///
/// <para><b>Mode is carried as-emitted.</b> The composer reads
/// <see cref="CalendarTimeAdvanced.Mode"/> at the source frame and passes it
/// through; side-effect subscribers (Gandalf shift alarms) gate on
/// <see cref="WorldMode.Live"/> at the side-effect boundary.</para>
/// </summary>
internal sealed class TimeOfDayShiftComposer : IComposer
{
    private readonly IShiftCatalog _catalog;
    private string? _lastEmittedSlug;

    public TimeOfDayShiftComposer(IShiftCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public IReadOnlyCollection<Type> Subscribes { get; } = new[] { typeof(CalendarTimeAdvanced) };

    public IReadOnlyList<IFrame> Observe(object eventPayload, IWorldClock clock)
    {
        if (eventPayload is not CalendarTimeAdvanced advanced)
        {
            return Array.Empty<IFrame>();
        }

        // Project the calendar tick's wall-clock into PG in-game time-of-day
        // and look up the active shift bucket. Both halves are pure (the
        // catalog is read-only after load; the projection uses the file-static
        // GameClock anchor), so the composer holds no mutable state beyond
        // the last-emitted-slug dedup.
        var slug = ResolveActiveSlug(advanced.Now);
        if (slug is null)
        {
            // Empty / malformed catalog — degrade silently. The shell chip
            // already null-guards on the same catalog (ShellViewModel
            // BuildNextShiftCountdown), so a stale or missing shifts.json
            // doesn't break the world's dispatch loop.
            return Array.Empty<IFrame>();
        }

        if (string.Equals(_lastEmittedSlug, slug, StringComparison.Ordinal))
        {
            return Array.Empty<IFrame>();
        }

        var prior = _lastEmittedSlug;
        _lastEmittedSlug = slug;

        // Carry the source tick's mode through so subscribers see a single
        // self-consistent mode for the transition — even if a Replaying →
        // Live flip is pending, the composer emits at the tick's mode and
        // the next tick (already Live) drives the next transition.
        return new IFrame[]
        {
            new Frame<TimeOfDayShift>(
                advanced.Now,
                new TimeOfDayShift(prior, slug, advanced.Now, advanced.Mode)),
        };
    }

    private string? ResolveActiveSlug(DateTimeOffset at)
    {
        var shifts = _catalog.Shifts;
        if (shifts.Count == 0) return null;

        var tod = GameClock.Project(at);
        // Shifts are stored in StartHour order (JsonShiftCatalog sorts on load).
        // Find the latest shift whose StartHour <= current hour; if none (we are
        // before the earliest StartHour of the day), the active shift is the
        // last one of the previous day — i.e. the highest-StartHour shift.
        string? active = null;
        foreach (var shift in shifts)
        {
            if (shift.StartHour <= tod.Hour) active = shift.Slug;
        }
        return active ?? shifts[^1].Slug;
    }
}
