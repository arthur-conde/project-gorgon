using FluentAssertions;
using Microsoft.Extensions.Logging;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

public class NoOpReferenceNavigatorTests
{
    [Fact]
    public void Open_WritesOneInfoEntry_ContainingKindAndInternalName()
    {
        var logger = new CapturingLogger<NoOpReferenceNavigator>();
        var navigator = new NoOpReferenceNavigator(logger);
        var entityRef = new EntityRef(EntityKind.Item, "CraftedLeatherBoots5");

        navigator.Open(entityRef);

        logger.Entries.Should().HaveCount(1);
        var entry = logger.Entries[0];
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain("Item");
        entry.Message.Should().Contain("CraftedLeatherBoots5");
    }

    [Fact]
    public void Open_DoesNotThrow()
    {
        var logger = new CapturingLogger<NoOpReferenceNavigator>();
        var navigator = new NoOpReferenceNavigator(logger);

        var act = () => navigator.Open(new EntityRef(EntityKind.Quest, "quest_10001"));

        act.Should().NotThrow();
    }

    // ---- New members (history primitives) ------------------------------------

    [Fact]
    public void Current_IsNull()
    {
        var navigator = new NoOpReferenceNavigator(new CapturingLogger<NoOpReferenceNavigator>());

        navigator.Current.Should().BeNull();
    }

    [Fact]
    public void CanGoBack_IsFalse()
    {
        var navigator = new NoOpReferenceNavigator(new CapturingLogger<NoOpReferenceNavigator>());

        navigator.CanGoBack.Should().BeFalse();
    }

    [Fact]
    public void CanGoForward_IsFalse()
    {
        var navigator = new NoOpReferenceNavigator(new CapturingLogger<NoOpReferenceNavigator>());

        navigator.CanGoForward.Should().BeFalse();
    }

    [Fact]
    public void CanOpen_ReturnsFalse_ForAnyEntity()
    {
        var navigator = new NoOpReferenceNavigator(new CapturingLogger<NoOpReferenceNavigator>());

        navigator.CanOpen(new EntityRef(EntityKind.Item, "SomeItem")).Should().BeFalse();
        navigator.CanOpen(new EntityRef(EntityKind.Npc, "SomeNpc")).Should().BeFalse();
        navigator.CanOpen(new EntityRef(EntityKind.Quest, "SomeQuest")).Should().BeFalse();
    }

    [Fact]
    public void Back_WritesOneInfoEntry_AndDoesNotThrow()
    {
        var logger = new CapturingLogger<NoOpReferenceNavigator>();
        var navigator = new NoOpReferenceNavigator(logger);

        var act = () => navigator.Back();

        act.Should().NotThrow();
        logger.Entries.Should().HaveCount(1);
        logger.Entries[0].Level.Should().Be(LogLevel.Information);
    }

    [Fact]
    public void Forward_WritesOneInfoEntry_AndDoesNotThrow()
    {
        var logger = new CapturingLogger<NoOpReferenceNavigator>();
        var navigator = new NoOpReferenceNavigator(logger);

        var act = () => navigator.Forward();

        act.Should().NotThrow();
        logger.Entries.Should().HaveCount(1);
        logger.Entries[0].Level.Should().Be(LogLevel.Information);
    }

    [Fact]
    public void Navigated_IsNeverFired_WhenOpenIsCalled()
    {
        var navigator = new NoOpReferenceNavigator(new CapturingLogger<NoOpReferenceNavigator>());
        var fired = false;
        navigator.Navigated += (_, _) => fired = true;

        navigator.Open(new EntityRef(EntityKind.Item, "AnyItem"));

        fired.Should().BeFalse("NoOpReferenceNavigator never fires the Navigated event");
    }

    [Fact]
    public void Navigated_IsNeverFired_WhenBackIsCalled()
    {
        var navigator = new NoOpReferenceNavigator(new CapturingLogger<NoOpReferenceNavigator>());
        var fired = false;
        navigator.Navigated += (_, _) => fired = true;

        navigator.Back();

        fired.Should().BeFalse("NoOpReferenceNavigator never fires the Navigated event");
    }

    [Fact]
    public void Navigated_IsNeverFired_WhenForwardIsCalled()
    {
        var navigator = new NoOpReferenceNavigator(new CapturingLogger<NoOpReferenceNavigator>());
        var fired = false;
        navigator.Navigated += (_, _) => fired = true;

        navigator.Forward();

        fired.Should().BeFalse("NoOpReferenceNavigator never fires the Navigated event");
    }

    // ---- minimal ILogger<T> stub -----------------------------------------------

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }
}
