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
