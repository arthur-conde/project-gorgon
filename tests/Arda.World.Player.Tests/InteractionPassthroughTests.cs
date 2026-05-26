using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

public class InteractionPassthroughTests
{
    private readonly SpyEventBus _bus = new();

    private static LogLineMetadata Meta() =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false);

    // ── EndInteractionHandler ────────────────────────────────────────────

    [Fact]
    public void EndInteraction_ParsesEntityId()
    {
        var handler = new EndInteractionHandler(_bus);
        var args = "(-158)";

        handler.Handle(args.AsSpan(), "", Meta());

        var e = _bus.Published<InteractionEnded>().Should().ContainSingle().Which;
        e.EntityId.Should().Be(-158);
    }

    [Fact]
    public void EndInteraction_PositiveEntityId()
    {
        var handler = new EndInteractionHandler(_bus);
        var args = "(42)";

        handler.Handle(args.AsSpan(), "", Meta());

        _bus.Published<InteractionEnded>().Should().ContainSingle()
            .Which.EntityId.Should().Be(42);
    }

    // ── DelayLoopHandler ─────────────────────────────────────────────────

    [Fact]
    public void DelayLoop_ParsesSecondsVerbAndText()
    {
        var handler = new DelayLoopHandler(_bus);
        var sourceLine = """LocalPlayer: ProcessDoDelayLoop(3, Gather, "Collecting Fruit...", 0, AbortIfAttacked, IsInteractorDelayLoop)""";
        var args = """(3, Gather, "Collecting Fruit...", 0, AbortIfAttacked, IsInteractorDelayLoop)""";

        handler.Handle(args.AsSpan(), sourceLine, Meta());

        var e = _bus.Published<DelayLoopStarted>().Should().ContainSingle().Which;
        e.Seconds.Should().Be(3.0);
        e.Verb.ToString().Should().Be("Gather");
        e.Text.ToString().Should().Be("Collecting Fruit...");
    }

    [Fact]
    public void DelayLoop_DecimalSeconds()
    {
        var handler = new DelayLoopHandler(_bus);
        var sourceLine = """LocalPlayer: ProcessDoDelayLoop(1.5, Eat, "Using Ranalon Salad", 5820, AbortIfAttacked)""";
        var args = """(1.5, Eat, "Using Ranalon Salad", 5820, AbortIfAttacked)""";

        handler.Handle(args.AsSpan(), sourceLine, Meta());

        var e = _bus.Published<DelayLoopStarted>().Should().ContainSingle().Which;
        e.Seconds.Should().BeApproximately(1.5, 0.001);
        e.Verb.ToString().Should().Be("Eat");
        e.Text.ToString().Should().Be("Using Ranalon Salad");
    }

    [Fact]
    public void DelayLoop_MemorySlicesIntoSourceLog()
    {
        var handler = new DelayLoopHandler(_bus);
        var sourceLine = """LocalPlayer: ProcessDoDelayLoop(10, UseTeleportationCircle, "Recalling Other Places", 3713, AbortIfAttacked)""";
        var args = sourceLine.AsSpan()["LocalPlayer: ProcessDoDelayLoop".Length..];

        handler.Handle(args, sourceLine, Meta());

        var e = _bus.Published<DelayLoopStarted>().Should().ContainSingle().Which;
        e.Verb.ToString().Should().Be("UseTeleportationCircle");
        e.Text.ToString().Should().Be("Recalling Other Places");
    }

    // ── WaitInteractionHandler ───────────────────────────────────────────

    [Fact]
    public void WaitInteraction_ParsesEntityIdAndBody()
    {
        var handler = new WaitInteractionHandler(_bus);
        var sourceLine = """LocalPlayer: ProcessWaitInteraction(-2, 500, "Filling Water Bottles...", "...")""";
        var args = """(-2, 500, "Filling Water Bottles...", "...")""";

        handler.Handle(args.AsSpan(), sourceLine, Meta());

        var e = _bus.Published<InteractionWaiting>().Should().ContainSingle().Which;
        e.EntityId.Should().Be(-2);
        e.Body.ToString().Should().Be("...");
    }

    [Fact]
    public void WaitInteraction_EmptyBody()
    {
        var handler = new WaitInteractionHandler(_bus);
        var sourceLine = """LocalPlayer: ProcessWaitInteraction(-45, 500, "", "")""";
        var args = """(-45, 500, "", "")""";

        handler.Handle(args.AsSpan(), sourceLine, Meta());

        var e = _bus.Published<InteractionWaiting>().Should().ContainSingle().Which;
        e.EntityId.Should().Be(-45);
        e.Body.Length.Should().Be(0);
    }

    // ── ScreenTextHandler (extended) ─────────────────────────────────────

    [Fact]
    public void ScreenText_EmitsObservedForCombatInfo()
    {
        var handler = new ScreenTextHandler(_bus);
        var sourceLine = """LocalPlayer: ProcessScreenText(CombatInfo, "You earned 5 Combat Wisdom: Killed Olugax")""";
        var args = """(CombatInfo, "You earned 5 Combat Wisdom: Killed Olugax")""";

        handler.Handle(args.AsSpan(), sourceLine, Meta());

        _bus.Published<ScreenTextErrorFrame>().Should().BeEmpty();
        var e = _bus.Published<ScreenTextObserved>().Should().ContainSingle().Which;
        e.Category.ToString().Should().Be("CombatInfo");
        e.Text.ToString().Should().Be("You earned 5 Combat Wisdom: Killed Olugax");
    }

    [Fact]
    public void ScreenText_EmitsObservedForImportantInfo()
    {
        var handler = new ScreenTextHandler(_bus);
        var sourceLine = """LocalPlayer: ProcessScreenText(ImportantInfo, "Iron Ore x3 collected!")""";
        var args = """(ImportantInfo, "Iron Ore x3 collected!")""";

        handler.Handle(args.AsSpan(), sourceLine, Meta());

        _bus.Published<ScreenTextErrorFrame>().Should().BeEmpty();
        var e = _bus.Published<ScreenTextObserved>().Should().ContainSingle().Which;
        e.Category.ToString().Should().Be("ImportantInfo");
        e.Text.ToString().Should().Be("Iron Ore x3 collected!");
    }

    [Fact]
    public void ScreenText_EmitsObservedForGeneralInfo()
    {
        var handler = new ScreenTextHandler(_bus);
        var sourceLine = """LocalPlayer: ProcessScreenText(GeneralInfo, "You've already looted this chest!")""";
        var args = """(GeneralInfo, "You've already looted this chest!")""";

        handler.Handle(args.AsSpan(), sourceLine, Meta());

        _bus.Published<ScreenTextErrorFrame>().Should().BeEmpty();
        var e = _bus.Published<ScreenTextObserved>().Should().ContainSingle().Which;
        e.Category.ToString().Should().Be("GeneralInfo");
    }

    [Fact]
    public void ScreenText_StillEmitsErrorFrameForErrorMessage()
    {
        var handler = new ScreenTextHandler(_bus);
        var args = """(ErrorMessage, "Something went wrong")""";

        handler.Handle(args.AsSpan(), args, Meta());

        _bus.Published<ScreenTextErrorFrame>().Should().ContainSingle();
        _bus.Published<ScreenTextObserved>().Should().ContainSingle(
            "ErrorMessage lines now emit both ScreenTextErrorFrame and ScreenTextObserved");
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
