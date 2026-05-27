using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

public class QuestTests
{
    private readonly SpyEventBus _bus = new();
    private readonly Quest _quest;

    public QuestTests()
    {
        _quest = new Quest(_bus);
    }

    private static LogLineMetadata Meta(bool isReplay = false) =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, isReplay);

    [Fact]
    public void NewQuest_EmitsQuestId()
    {
        var args = "(\"New Quest: <<<quest_12345_Name>>>\", \"body\")".AsSpan();
        _quest.Handle(args, default, "source", Meta());

        _bus.Published<QuestOffered>().Should().ContainSingle()
            .Which.QuestId.Should().Be(12345);
    }

    [Fact]
    public void NonQuestBook_DoesNotEmit()
    {
        var args = "(\"Some Lore Book Title\", \"body\")".AsSpan();
        _quest.Handle(args, default, "source", Meta());

        _bus.Published<QuestOffered>().Should().BeEmpty();
    }

    [Fact]
    public void MalformedQuestId_DoesNotEmit()
    {
        var args = "(\"New Quest: <<<quest_abc_Name>>>\", \"body\")".AsSpan();
        _quest.Handle(args, default, "source", Meta());

        _bus.Published<QuestOffered>().Should().BeEmpty();
    }

    private sealed class SpyEventBus : IDomainEventSubscriber, IDomainEventPublisher
    {
        private readonly Dictionary<Type, List<object>> _published = [];

        public IDisposable Subscribe<T>(Action<T> handler) where T : struct => new NoopDisposable();

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

        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }
}
