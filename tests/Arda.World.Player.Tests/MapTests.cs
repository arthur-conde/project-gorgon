using System.Collections.Frozen;
using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

public class MapTests
{
    private readonly SpyEventBus _bus = new();
    private readonly Map _map;

    public MapTests()
    {
        var pool = new InternPool(FrozenDictionary<string, string>.Empty);
        _map = new Map(_bus, pool);
    }

    private static LogLineMetadata Meta(bool isReplay = false) =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, isReplay);

    /// <summary>
    /// Simulates what the DispatchTable does: extracts args from the source line
    /// and calls Handle. For LOADING_LEVEL, args is the area key (or empty).
    /// </summary>
    private void DispatchLoadingLevel(string areaArg, LogLineMetadata? meta = null)
    {
        var source = string.IsNullOrEmpty(areaArg)
            ? "LOADING LEVEL"
            : $"LOADING LEVEL {areaArg}";
        // Args for LOADING_LEVEL: everything after "LOADING LEVEL " (the area key)
        ReadOnlySpan<char> args = string.IsNullOrEmpty(areaArg)
            ? ReadOnlySpan<char>.Empty
            : areaArg.AsSpan();
        _map.Handle(args, default, source, meta ?? Meta());
    }

    /// <summary>
    /// Simulates dispatch for InitializingArea. Args format: "(502934): AreaKey"
    /// </summary>
    private void DispatchInitializingArea(string areaKey, LogLineMetadata? meta = null)
    {
        var source = $"!!! Initializing area! (502934): {areaKey}";
        // Args for InitializingArea: everything after "!!! Initializing area! "
        var args = $"(502934): {areaKey}".AsSpan();
        _map.Handle(args, default, source, meta ?? Meta());
    }

    [Fact]
    public void LoadingLevel_SetsCurrentArea_OnInitializingArea()
    {
        DispatchLoadingLevel("AreaSerbule");
        DispatchInitializingArea("AreaSerbule");

        _map.CurrentArea.Should().Be("AreaSerbule");
        _bus.Published<AreaChanged>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                PreviousArea = (string?)null,
                CurrentArea = "AreaSerbule"
            });
    }

    [Fact]
    public void LoadingLevel_DoesNotEmit_UntilInitialized()
    {
        DispatchLoadingLevel("AreaSerbule");

        _map.CurrentArea.Should().BeNull();
        _map.PendingArea.Should().Be("AreaSerbule");
        _bus.Published<AreaChanged>().Should().BeEmpty();
    }

    [Fact]
    public void BareLoadingLevel_ClearsArea()
    {
        DispatchLoadingLevel("AreaSerbule");
        DispatchInitializingArea("AreaSerbule");
        _bus.Clear();

        DispatchLoadingLevel("");

        _map.CurrentArea.Should().BeNull();
        _bus.Published<AreaChanged>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                PreviousArea = "AreaSerbule",
                CurrentArea = (string?)null
            });
    }

    [Fact]
    public void ChooseCharacter_ClearsArea()
    {
        DispatchLoadingLevel("AreaSerbule");
        DispatchInitializingArea("AreaSerbule");
        _bus.Clear();

        DispatchLoadingLevel("ChooseCharacter");

        _map.CurrentArea.Should().BeNull();
        _bus.Published<AreaChanged>().Should().ContainSingle();
    }

    [Fact]
    public void ReconnectToServer_ClearsArea()
    {
        DispatchLoadingLevel("AreaSerbule");
        DispatchInitializingArea("AreaSerbule");
        _bus.Clear();

        DispatchLoadingLevel("ReconnectToServer");

        _map.CurrentArea.Should().BeNull();
        _bus.Published<AreaChanged>().Should().ContainSingle();
    }

    [Fact]
    public void InitializingArea_WithoutPriorLoadingLevel_StillEmits()
    {
        DispatchInitializingArea("AreaSerbule");

        _map.CurrentArea.Should().Be("AreaSerbule");
        _bus.Published<AreaChanged>().Should().ContainSingle()
            .Which.CurrentArea.Should().Be("AreaSerbule");
    }

    [Fact]
    public void AreaChange_InternsSameKey_WhenPoolSeeded()
    {
        // Seed pool with known area keys to test interning behavior
        const string serbuleKey = "AreaSerbule";
        var seeded = new Dictionary<string, string> { [serbuleKey] = serbuleKey }
            .ToFrozenDictionary(StringComparer.Ordinal);
        var pool = new InternPool(seeded);
        var map = new Map(_bus, pool);

        var source1 = "LOADING LEVEL AreaSerbule";
        map.Handle("AreaSerbule".AsSpan(), default, source1, Meta());
        var initSource = "!!! Initializing area! (502934): AreaSerbule";
        map.Handle("(502934): AreaSerbule".AsSpan(), default, initSource, Meta());

        var first = map.CurrentArea;

        var source2 = "LOADING LEVEL AreaCasino";
        map.Handle("AreaCasino".AsSpan(), default, source2, Meta());
        var initSource2 = "!!! Initializing area! (100): AreaCasino";
        map.Handle("(100): AreaCasino".AsSpan(), default, initSource2, Meta());

        var source3 = "LOADING LEVEL AreaSerbule";
        map.Handle("AreaSerbule".AsSpan(), default, source3, Meta());
        var initSource3 = "!!! Initializing area! (502934): AreaSerbule";
        map.Handle("(502934): AreaSerbule".AsSpan(), default, initSource3, Meta());

        var second = map.CurrentArea;

        ReferenceEquals(first, second).Should().BeTrue(
            "InternPool should return the same string instance for repeated area keys");
        ReferenceEquals(first, serbuleKey).Should().BeTrue(
            "Interned value should be the exact reference from the seeded dictionary");
    }

    [Fact]
    public void IsReplay_DoesNotSuppressStateUpdate()
    {
        var replayMeta = Meta(isReplay: true);
        DispatchLoadingLevel("AreaSerbule", replayMeta);
        DispatchInitializingArea("AreaSerbule", replayMeta);

        _map.CurrentArea.Should().Be("AreaSerbule");
        _bus.Published<AreaChanged>().Should().ContainSingle()
            .Which.Metadata.IsReplay.Should().BeTrue();
    }

    [Fact]
    public void AreaToArea_Transition_ReportsPreviousArea()
    {
        DispatchLoadingLevel("AreaSerbule");
        DispatchInitializingArea("AreaSerbule");
        _bus.Clear();

        DispatchLoadingLevel("AreaCasino");
        DispatchInitializingArea("AreaCasino");

        _map.CurrentArea.Should().Be("AreaCasino");
        _bus.Published<AreaChanged>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                PreviousArea = "AreaSerbule",
                CurrentArea = "AreaCasino"
            });
    }

    [Fact]
    public void PendingConfirmed_Mismatch_InitializingAreaWins()
    {
        DispatchLoadingLevel("AreaSerbule");
        DispatchInitializingArea("AreaCasino");

        _map.CurrentArea.Should().Be("AreaCasino");
        _map.PendingArea.Should().BeNull();
    }

    [Fact]
    public void DuplicateAreaConfirmation_DoesNotEmit()
    {
        DispatchLoadingLevel("AreaSerbule");
        DispatchInitializingArea("AreaSerbule");
        _bus.Clear();

        DispatchInitializingArea("AreaSerbule");

        _bus.Published<AreaChanged>().Should().BeEmpty();
        _map.CurrentArea.Should().Be("AreaSerbule");
    }

    [Fact]
    public void MalformedInitializingArea_NoColonSpace_IsIgnored()
    {
        _map.Handle("(502934)AreaSerbule".AsSpan(), default, "malformed", Meta());

        _map.CurrentArea.Should().BeNull();
        _bus.Published<AreaChanged>().Should().BeEmpty();
    }

    [Fact]
    public void BareLoadingLevel_WhenAlreadyNull_DoesNotEmit()
    {
        _map.CurrentArea.Should().BeNull();

        DispatchLoadingLevel("");

        _bus.Published<AreaChanged>().Should().BeEmpty();
    }

    private sealed class SpyEventBus : IDomainEventSubscriber, IDomainEventPublisher
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
