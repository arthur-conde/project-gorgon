using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

public class CalendarTests
{
    private readonly SpyEventBus _bus = new();

    private static readonly IReadOnlyList<(string Slug, int StartHour)> DefaultShifts =
    [
        ("midnight", 0),
        ("dawn", 5),
        ("morning", 8),
        ("afternoon", 12),
        ("dusk", 17),
        ("night", 20),
    ];

    private Calendar CreateCalendar(
        Func<DateTimeOffset, int>? projectToGameHour = null,
        IReadOnlyList<(string Slug, int StartHour)>? shifts = null) =>
        new(_bus, projectToGameHour ?? (_ => 10), shifts ?? DefaultShifts);

    private static LogLineMetadata Meta(DateTimeOffset? ts = null, bool isReplay = false) =>
        new(ts ?? DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, isReplay);

    [Fact]
    public void AdvancingTimestamp_Emits_CalendarTimeAdvanced()
    {
        var calendar = CreateCalendar();
        var ts = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);

        calendar.Observe("", Meta(ts));

        _bus.Published<CalendarTimeAdvanced>().Should().ContainSingle()
            .Which.Now.Should().Be(ts);
    }

    [Fact]
    public void SameSecondTimestamp_DoesNotDoubleEmit()
    {
        var calendar = CreateCalendar();
        var ts = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
        var sameSecond = ts.AddMilliseconds(500);

        calendar.Observe("", Meta(ts));
        calendar.Observe("", Meta(sameSecond));

        _bus.Published<CalendarTimeAdvanced>().Should().ContainSingle();
    }

    [Fact]
    public void DifferentSecondTimestamp_EmitsAgain()
    {
        var calendar = CreateCalendar();
        var ts1 = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
        var ts2 = ts1.AddSeconds(1);

        calendar.Observe("", Meta(ts1));
        calendar.Observe("", Meta(ts2));

        _bus.Published<CalendarTimeAdvanced>().Should().HaveCount(2);
    }

    [Fact]
    public void NullTimestamp_IsIgnored()
    {
        var calendar = CreateCalendar();

        calendar.Observe("", new LogLineMetadata(null, DateTimeOffset.UtcNow, false));

        _bus.Published<CalendarTimeAdvanced>().Should().BeEmpty();
        calendar.LastTimestamp.Should().BeNull();
    }

    [Fact]
    public void ShiftBoundaryCrossing_Emits_TimeOfDayShifted()
    {
        var gameHour = 10;
        var calendar = CreateCalendar(projectToGameHour: _ => gameHour);
        var ts1 = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
        var ts2 = ts1.AddSeconds(1);

        calendar.Observe("", Meta(ts1));
        _bus.Clear();

        gameHour = 12;
        calendar.Observe("", Meta(ts2));

        _bus.Published<TimeOfDayShifted>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                From = "morning",
                To = "afternoon",
                At = ts2
            });
    }

    [Fact]
    public void FirstObservation_Emits_TimeOfDayShifted_WithNullFrom()
    {
        var calendar = CreateCalendar(projectToGameHour: _ => 10);
        var ts = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);

        calendar.Observe("", Meta(ts));

        _bus.Published<TimeOfDayShifted>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                From = (string?)null,
                To = "morning",
                At = ts
            });
    }

    [Fact]
    public void SameShift_DoesNotDoubleEmit()
    {
        var calendar = CreateCalendar(projectToGameHour: _ => 10);
        var ts1 = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
        var ts2 = ts1.AddSeconds(1);

        calendar.Observe("", Meta(ts1));
        calendar.Observe("", Meta(ts2));

        _bus.Published<TimeOfDayShifted>().Should().ContainSingle();
    }

    [Fact]
    public void EmptyShiftList_DoesNotCrash()
    {
        var calendar = CreateCalendar(shifts: []);
        var ts = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);

        calendar.Observe("", Meta(ts));

        _bus.Published<CalendarTimeAdvanced>().Should().ContainSingle();
        _bus.Published<TimeOfDayShifted>().Should().BeEmpty();
    }

    [Fact]
    public void LastTimestamp_TracksLatestObservation()
    {
        var calendar = CreateCalendar();
        var ts1 = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
        var ts2 = ts1.AddSeconds(5);

        calendar.LastTimestamp.Should().BeNull();

        calendar.Observe("", Meta(ts1));
        calendar.LastTimestamp.Should().Be(ts1);

        calendar.Observe("", Meta(ts2));
        calendar.LastTimestamp.Should().Be(ts2);
    }

    [Fact]
    public void CurrentShift_TracksActiveShift()
    {
        var gameHour = 10;
        var calendar = CreateCalendar(projectToGameHour: _ => gameHour);

        calendar.CurrentShift.Should().BeNull();

        var ts = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
        calendar.Observe("", Meta(ts));
        calendar.CurrentShift.Should().Be("morning");

        gameHour = 20;
        calendar.Observe("", Meta(ts.AddSeconds(1)));
        calendar.CurrentShift.Should().Be("night");
    }

    [Fact]
    public void HourBeforeFirstShift_WrapsToLastShift()
    {
        var shifts = new List<(string Slug, int StartHour)>
        {
            ("dawn", 5),
            ("night", 20),
        };
        var calendar = CreateCalendar(projectToGameHour: _ => 3, shifts: shifts);
        var ts = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);

        calendar.Observe("", Meta(ts));

        _bus.Published<TimeOfDayShifted>().Should().ContainSingle()
            .Which.To.Should().Be("night");
    }

    [Fact]
    public void Metadata_IsPreserved_OnEmittedEvents()
    {
        var calendar = CreateCalendar(projectToGameHour: _ => 10);
        var ts = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
        var meta = Meta(ts, isReplay: true);

        calendar.Observe("", meta);

        _bus.Published<CalendarTimeAdvanced>().Should().ContainSingle()
            .Which.Metadata.IsReplay.Should().BeTrue();
        _bus.Published<TimeOfDayShifted>().Should().ContainSingle()
            .Which.Metadata.IsReplay.Should().BeTrue();
    }

    private sealed class SpyEventBus : IDomainEventBus
    {
        private readonly Dictionary<Type, List<object>> _published = [];

        public IDisposable Subscribe<T>(Action<T> handler) where T : struct
            => new NoopDisposable();

        public void Publish<T>(T domainEvent) where T : struct
        {
            if (!_published.TryGetValue(typeof(T), out var list))
            {
                list = [];
                _published[typeof(T)] = list;
            }
            list.Add(domainEvent);
        }

        public List<T> Published<T>() where T : struct
        {
            if (_published.TryGetValue(typeof(T), out var list))
                return list.Cast<T>().ToList();
            return [];
        }

        public void Clear() => _published.Clear();

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
