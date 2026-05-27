using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

public class PositionTests
{
    private readonly SpyEventBus _bus = new();
    private readonly Position _position;

    public PositionTests()
    {
        _position = new Position(_bus);
    }

    private static LogLineMetadata Meta(bool isReplay = false) =>
        new(new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero), DateTimeOffset.UtcNow, isReplay);

    private void DispatchNewPosition(string args)
    {
        _position.Handle(args.AsSpan(), default, $"LocalPlayer: ProcessNewPosition{args}", Meta());
    }

    private void DispatchAddPlayer(string args)
    {
        _position.Handle(args.AsSpan(), default, $"LocalPlayer: ProcessAddPlayer{args}", Meta());
    }

    // --- ProcessNewPosition: nested tuple ((x, y, z), ...) ---

    [Fact]
    public void NewPosition_ExtractsCoordinates()
    {
        DispatchNewPosition("((100.5, 200.75, 300.25), (0.0, 0.96, 0.0, -0.27))");

        _position.X.Should().Be(100.5);
        _position.Y.Should().Be(200.75);
        _position.Z.Should().Be(300.25);
        _position.Source.Should().Be(PositionSource.Movement);
    }

    [Fact]
    public void NewPosition_EmitsEvent()
    {
        DispatchNewPosition("((100.5, 200.75, 300.25), (0.0, 0.96, 0.0, -0.27))");

        _bus.Published<PlayerPositionChanged>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                X = 100.5,
                Y = 200.75,
                Z = 300.25,
                Source = PositionSource.Movement
            });
    }

    [Fact]
    public void NewPosition_NegativeCoordinates()
    {
        DispatchNewPosition("((-42.5, -100.0, -0.75), (0.0, 1.0, 0.0, 0.0))");

        _position.X.Should().Be(-42.5);
        _position.Y.Should().Be(-100.0);
        _position.Z.Should().Be(-0.75);
    }

    [Fact]
    public void NewPosition_LargeCoordinates()
    {
        DispatchNewPosition("((2550.51, 299.58, 1998.36), (0.0, 0.96, 0.0, -0.27))");

        _position.X.Should().Be(2550.51);
        _position.Y.Should().Be(299.58);
        _position.Z.Should().Be(1998.36);
    }

    [Fact]
    public void NewPosition_MissingInnerParen_NoEventEmitted()
    {
        DispatchNewPosition("(100.5, 200.75, 300.25)");

        _position.X.Should().BeNull();
        _bus.Published<PlayerPositionChanged>().Should().BeEmpty();
    }

    [Fact]
    public void NewPosition_NonNumeric_NoEventEmitted()
    {
        DispatchNewPosition("((abc, 200.75, 300.25), (0.0, 1.0, 0.0, 0.0))");

        _position.X.Should().BeNull();
        _bus.Published<PlayerPositionChanged>().Should().BeEmpty();
    }

    [Fact]
    public void NewPosition_MissingClosingParen_NoEventEmitted()
    {
        DispatchNewPosition("((100.5, 200.75, 300.25");

        _position.X.Should().BeNull();
        _bus.Published<PlayerPositionChanged>().Should().BeEmpty();
    }

    [Fact]
    public void NewPosition_TwoCommaOnly_NoEventEmitted()
    {
        DispatchNewPosition("((100.5, 200.75), (0.0, 1.0, 0.0, 0.0))");

        _position.X.Should().BeNull();
        _bus.Published<PlayerPositionChanged>().Should().BeEmpty();
    }

    // --- ProcessAddPlayer: System.String[] marker scan ---

    [Fact]
    public void SpawnPosition_ExtractsCoordinates()
    {
        DispatchAddPlayer(
            "(-1107394649, 25042203, \"@model\", \"CharName\", \"desc\", " +
            "System.String[], (2550.51, 299.58, 1998.36), (0.0, 0.96, 0.0, -0.27), Idle, Standing, 0, 0, True)");

        _position.X.Should().Be(2550.51);
        _position.Y.Should().Be(299.58);
        _position.Z.Should().Be(1998.36);
        _position.Source.Should().Be(PositionSource.Spawn);
    }

    [Fact]
    public void SpawnPosition_EmitsEvent()
    {
        DispatchAddPlayer(
            "(-1107394649, 25042203, \"@model\", \"CharName\", \"desc\", " +
            "System.String[], (2550.51, 299.58, 1998.36), (0.0, 0.96, 0.0, -0.27), Idle, Standing, 0, 0, True)");

        _bus.Published<PlayerPositionChanged>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                X = 2550.51,
                Y = 299.58,
                Z = 1998.36,
                Source = PositionSource.Spawn
            });
    }

    [Fact]
    public void SpawnPosition_NegativeCoordinates()
    {
        DispatchAddPlayer(
            "(1, 2, \"@m\", \"C\", \"d\", System.String[], (-100.0, -50.5, -25.25), (0, 0, 0, 0), Idle, Standing, 0, 0, True)");

        _position.X.Should().Be(-100.0);
        _position.Y.Should().Be(-50.5);
        _position.Z.Should().Be(-25.25);
    }

    [Fact]
    public void SpawnPosition_NoMarker_NoEventEmitted()
    {
        DispatchAddPlayer("(1, 2, \"@m\", \"C\", \"d\", (100, 200, 300))");

        _position.X.Should().BeNull();
        _bus.Published<PlayerPositionChanged>().Should().BeEmpty();
    }

    [Fact]
    public void SpawnPosition_NoParenAfterMarker_NoEventEmitted()
    {
        DispatchAddPlayer("(1, 2, \"@m\", \"C\", \"d\", System.String[])");

        _position.X.Should().BeNull();
        _bus.Published<PlayerPositionChanged>().Should().BeEmpty();
    }

    // --- State lifecycle ---

    [Fact]
    public void MeasuredAt_SetFromTimestamp()
    {
        var ts = new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);
        var meta = new LogLineMetadata(ts, DateTimeOffset.UtcNow, false);
        _position.Handle("((1, 2, 3), (0, 0, 0, 0))".AsSpan(), default, "source", meta);

        _position.MeasuredAt.Should().Be(ts);
    }

    [Fact]
    public void MeasuredAt_FallsBackToReadOn_WhenTimestampNull()
    {
        var readOn = DateTimeOffset.UtcNow;
        var meta = new LogLineMetadata(null, readOn, false);
        _position.Handle("((1, 2, 3), (0, 0, 0, 0))".AsSpan(), default, "source", meta);

        _position.MeasuredAt.Should().Be(readOn);
    }

    [Fact]
    public void LatestPosition_Wins()
    {
        DispatchNewPosition("((10, 20, 30), (0, 0, 0, 0))");
        DispatchNewPosition("((40, 50, 60), (0, 0, 0, 0))");

        _position.X.Should().Be(40);
        _position.Y.Should().Be(50);
        _position.Z.Should().Be(60);
    }

    [Fact]
    public void MovementThenSpawn_SourceUpdated()
    {
        DispatchNewPosition("((10, 20, 30), (0, 0, 0, 0))");
        _position.Source.Should().Be(PositionSource.Movement);

        DispatchAddPlayer(
            "(1, 2, \"@m\", \"C\", \"d\", System.String[], (40, 50, 60), (0, 0, 0, 0), Idle, Standing, 0, 0, True)");
        _position.Source.Should().Be(PositionSource.Spawn);
    }

    [Fact]
    public void Reset_ClearsState()
    {
        DispatchNewPosition("((10, 20, 30), (0, 0, 0, 0))");
        _position.Reset();

        _position.X.Should().BeNull();
        _position.Y.Should().BeNull();
        _position.Z.Should().BeNull();
        _position.MeasuredAt.Should().BeNull();
        _position.Source.Should().BeNull();
    }

    [Fact]
    public void Metadata_IsReplay_Preserved()
    {
        var meta = new LogLineMetadata(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true);
        _position.Handle("((1, 2, 3), (0, 0, 0, 0))".AsSpan(), default, "source", meta);

        _bus.Published<PlayerPositionChanged>().Should().ContainSingle()
            .Which.Metadata.IsReplay.Should().BeTrue();
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
