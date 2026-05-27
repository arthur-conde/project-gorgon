using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

public class BookPassthroughTests
{
    private readonly SpyEventBus _bus = new();

    private static LogLineMetadata Meta() =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false);

    // ── FoodsConsumedReport ──────────────────────────────────────────────

    [Fact]
    public void ProcessBook_FoodsConsumed_EmitsReport()
    {
        var handler = new ProcessBookHandler(_bus);
        var sourceLine = """LocalPlayer: ProcessBook("Skill Info", "Foods Consumed:\n\n  Guava: 42\n  Apple: 10", "icon", "SkillReport")""";
        var args = """("Skill Info", "Foods Consumed:\n\n  Guava: 42\n  Apple: 10", "icon", "SkillReport")""";

        handler.Handle(args.AsSpan(), default, sourceLine, Meta());

        _bus.Published<WordOfPowerDiscovered>().Should().BeEmpty();
        _bus.Published<BookOpened>().Should().BeEmpty();
        var e = _bus.Published<FoodsConsumedReport>().Should().ContainSingle().Which;
        e.Body.ToString().Should().Contain("Foods Consumed");
    }

    // ── WordOfPowerDiscovered ────────────────────────────────────────────

    [Fact]
    public void ProcessBook_WordOfPower_EmitsDiscovery()
    {
        var handler = new ProcessBookHandler(_bus);
        var body = @"You've discovered a word of power: <sel>ABCDEF</sel> blah <b><size=125%>Word of Power: Fire Bolt</size></b>\nA fiery bolt of energy\n\n<i>more stuff";
        var sourceLine = $"""LocalPlayer: ProcessBook("You discovered a word of power!", "{body}", "icon")""";
        var args = $"""("You discovered a word of power!", "{body}", "icon")""";

        handler.Handle(args.AsSpan(), default, sourceLine, Meta());

        _bus.Published<FoodsConsumedReport>().Should().BeEmpty();
        _bus.Published<BookOpened>().Should().BeEmpty();
        var e = _bus.Published<WordOfPowerDiscovered>().Should().ContainSingle().Which;
        e.Code.ToString().Should().Be("ABCDEF");
        e.Effect.ToString().Should().Be("Fire Bolt");
        e.Description.ToString().Should().Be("A fiery bolt of energy");
    }

    [Fact]
    public void ProcessBook_WordOfPower_MultiWordEffect()
    {
        var handler = new ProcessBookHandler(_bus);
        var body = @"You've discovered a word of power: <sel>XYZZY</sel> stuff <b><size=125%>Word of Power: Cold Sphere III</size></b>\nLaunches a freezing sphere\n\n<i>info";
        var sourceLine = $"""LocalPlayer: ProcessBook("You discovered a word of power!", "{body}", "icon")""";
        var args = $"""("You discovered a word of power!", "{body}", "icon")""";

        handler.Handle(args.AsSpan(), default, sourceLine, Meta());

        var e = _bus.Published<WordOfPowerDiscovered>().Should().ContainSingle().Which;
        e.Code.ToString().Should().Be("XYZZY");
        e.Effect.ToString().Should().Be("Cold Sphere III");
        e.Description.ToString().Should().Be("Launches a freezing sphere");
    }

    // ── Generic BookOpened ───────────────────────────────────────────────

    [Fact]
    public void ProcessBook_Generic_EmitsBookOpened()
    {
        var handler = new ProcessBookHandler(_bus);
        var sourceLine = """LocalPlayer: ProcessBook("Some Book Title", "Book body content here", "icon")""";
        var args = """("Some Book Title", "Book body content here", "icon")""";

        handler.Handle(args.AsSpan(), default, sourceLine, Meta());

        _bus.Published<FoodsConsumedReport>().Should().BeEmpty();
        _bus.Published<WordOfPowerDiscovered>().Should().BeEmpty();
        var e = _bus.Published<BookOpened>().Should().ContainSingle().Which;
        e.Title.ToString().Should().Be("Some Book Title");
        e.Body.ToString().Should().Be("Book body content here");
    }

    [Fact]
    public void ProcessBook_Generic_MemorySlicesIntoSourceLog()
    {
        var handler = new ProcessBookHandler(_bus);
        var sourceLine = """LocalPlayer: ProcessBook("MyTitle", "MyBody", "icon")""";
        var args = sourceLine.AsSpan()["LocalPlayer: ProcessBook".Length..];

        handler.Handle(args, default, sourceLine, Meta());

        var e = _bus.Published<BookOpened>().Should().ContainSingle().Which;
        e.Title.ToString().Should().Be("MyTitle");
        e.Body.ToString().Should().Be("MyBody");
    }

    // ── SpyEventBus ─────────────────────────────────────────────────────

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

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
