using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

public class SessionTests
{
    private readonly SpyEventBus _bus = new();
    private readonly Session _session;

    public SessionTests()
    {
        _session = new Session(_bus);
    }

    private static LogLineMetadata Meta(bool isReplay = false) =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, isReplay);

    /// <summary>
    /// Simulates ProcessAddPlayer dispatch. Args format:
    /// (entityId, arg2, "charId", "CharacterName", x, y, z, heading, ...)
    /// </summary>
    private void Dispatch(string charName, long entityId = 99, string charId = "12345")
    {
        var args = $"({entityId}, 7, \"{charId}\", \"{charName}\", 100.0, 0.0, 200.0, 1.5)".AsSpan();
        _session.Handle(args, "source", Meta());
    }

    [Fact]
    public void AddPlayer_ExtractsCharacterName()
    {
        Dispatch("TestCharacter");

        _session.ActiveCharacter.Should().Be("TestCharacter");
        _bus.Published<SessionStarted>().Should().ContainSingle()
            .Which.CharacterName.Should().Be("TestCharacter");
    }

    [Fact]
    public void MultipleAddPlayer_UpdatesName()
    {
        Dispatch("FirstChar");
        _bus.Clear();

        Dispatch("SecondChar");

        _session.ActiveCharacter.Should().Be("SecondChar");
        _bus.Published<SessionStarted>().Should().ContainSingle()
            .Which.CharacterName.Should().Be("SecondChar");
    }

    [Fact]
    public void Reset_ClearsActiveCharacter()
    {
        Dispatch("TestCharacter");
        _session.Reset();

        _session.ActiveCharacter.Should().BeNull();
    }

    private sealed class SpyEventBus : IDomainEventBus
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
