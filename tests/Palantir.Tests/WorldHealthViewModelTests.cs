using Arda.Contracts.State.Health;
using FluentAssertions;
using Palantir.ViewModels;
using Xunit;

namespace Palantir.Tests;

/// <summary>
/// Pins the per-mode overall-status copy and per-row mode-text mapping for
/// the Palantir World Health detail page after #856 reshaped the bindings
/// (was: PlayerDegraded/ChatDegraded booleans; now: Mode-driven).
/// </summary>
public sealed class WorldHealthViewModelTests
{
    private sealed class FakeHealth : IWorldHealthView
    {
        public WorldHealth Player { get; set; } = new(null, 0, WorldMode.Replaying, TimeSpan.Zero);
        public WorldHealth Chat { get; set; } = new(null, 0, WorldMode.Replaying, TimeSpan.Zero);
        public bool AllLive { get; set; }
        public GrammarBreak? Break { get; set; }
        public bool IsHalted { get; set; }
        public bool IsTolerantBreakActive { get; set; }
        public int ObservedBreakCount { get; set; }

        public event EventHandler? Changed;

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private static Action<Action> Synchronous() => a => a();

    [Fact]
    public void BothLive_OverallStatusIsHealthy()
    {
        var health = new FakeHealth
        {
            Player = new(null, 100, WorldMode.Live, TimeSpan.FromSeconds(1)),
            Chat = new(null, 50, WorldMode.Live, TimeSpan.FromSeconds(1)),
            AllLive = true,
        };

        var vm = new WorldHealthViewModel(health, Synchronous());

        vm.OverallStatus.Should().Be("Live — healthy");
        vm.PlayerMode.Should().Be(WorldMode.Live);
        vm.ChatMode.Should().Be(WorldMode.Live);
        vm.AllLive.Should().BeTrue();
    }

    [Fact]
    public void OneStalled_OverallStatusIsTailerStalled()
    {
        var health = new FakeHealth
        {
            Player = new(null, 100, WorldMode.Live, TimeSpan.FromSeconds(1)),
            Chat = new(null, 50, WorldMode.Stalled, TimeSpan.FromSeconds(10)),
            AllLive = false, // Stalled does NOT count as live (design lock #9)
        };

        var vm = new WorldHealthViewModel(health, Synchronous());

        vm.OverallStatus.Should().Be("Tailer stalled");
        vm.ChatMode.Should().Be(WorldMode.Stalled);
        vm.AllLive.Should().BeFalse();
    }

    [Fact]
    public void Halted_OverallStatusIsHaltedRegardlessOfOtherDriver()
    {
        var health = new FakeHealth
        {
            Player = new(null, 100, WorldMode.Halted, TimeSpan.Zero),
            Chat = new(null, 50, WorldMode.Halted, TimeSpan.Zero),
            IsHalted = true,
        };

        var vm = new WorldHealthViewModel(health, Synchronous());

        vm.OverallStatus.Should().Be("Halted — grammar break");
        vm.PlayerMode.Should().Be(WorldMode.Halted);
    }

    [Fact]
    public void Replaying_OverallStatusIsReplaying()
    {
        var vm = new WorldHealthViewModel(new FakeHealth(), Synchronous());

        vm.OverallStatus.Should().Be("Replaying log history…");
    }

    [Fact]
    public void ChangedEvent_RefreshesProperties()
    {
        var health = new FakeHealth();
        var vm = new WorldHealthViewModel(health, Synchronous());
        vm.PlayerMode.Should().Be(WorldMode.Replaying);

        health.Player = new(null, 100, WorldMode.Live, TimeSpan.FromSeconds(1));
        health.AllLive = false; // Chat still Replaying
        health.RaiseChanged();

        vm.PlayerMode.Should().Be(WorldMode.Live);
    }

    [Fact]
    public void Dispose_DetachesFromHealth()
    {
        var health = new FakeHealth();
        var vm = new WorldHealthViewModel(health, Synchronous());

        vm.Dispose();
        health.Player = new(null, 100, WorldMode.Stalled, TimeSpan.FromSeconds(10));
        health.RaiseChanged();

        vm.PlayerMode.Should().Be(WorldMode.Replaying, "disposed VM stops updating");
    }
}
