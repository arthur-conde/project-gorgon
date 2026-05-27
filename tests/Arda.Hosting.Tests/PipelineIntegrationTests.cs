using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arda.Hosting.Tests;

/// <summary>
/// End-to-end pipeline integration tests. Replays a short log segment through
/// the full L2 (WorldDriver) → L3 (dispatch + handlers) chain and verifies
/// composed state via the public state interfaces.
/// </summary>
public class PipelineIntegrationTests
{
    /// <summary>
    /// A login sequence: zone load, player spawn, skills, and an inventory item.
    /// Verifies that all handlers are wired and produce the expected state.
    /// </summary>
    [Fact]
    public async Task LoginSequence_ProducesExpectedState()
    {
        var lines = new[]
        {
            "[14:30:01] LOADING LEVEL AreaSerbule",
            "[14:30:02] !!! Initializing area! (502934): AreaSerbule",
            "[14:30:03] LocalPlayer: ProcessAddPlayer(-1107394649, 25042203, \"@model\", \"TestCharacter\", \"desc\", System.String[], (2550.51, 299.58, 1998.36), (0.0, 0.96, 0.0, -0.27), Idle, Standing, 0, 0, True)",
            "[14:30:04] LocalPlayer: ProcessLoadSkills({type=Sword,raw=42,bonus=0,xp=100,tnl=500,max=50}, {type=Archery,raw=15,bonus=0,xp=50,tnl=200,max=50})",
            "[14:30:05] LocalPlayer: ProcessAddItem(GoblinCap(84741837), -1, False)",
            "[14:30:06] LocalPlayer: ProcessSetWeather(\"Clear\", True)",
        };

        var (sp, driver) = BuildPipeline(lines);
        await driver.RunAsync(CancellationToken.None);

        var area = sp.GetRequiredService<IAreaState>();
        area.CurrentArea.Should().Be("AreaSerbule");

        var session = sp.GetRequiredService<ISessionState>();
        session.ActiveCharacter.Should().Be("TestCharacter");

        var position = sp.GetRequiredService<IPositionState>();
        position.X.Should().Be(2550.51);
        position.Y.Should().Be(299.58);
        position.Z.Should().Be(1998.36);

        var skills = sp.GetRequiredService<ISkillState>();
        skills.Skills.Should().ContainKey("Sword");
        skills.Skills["Sword"].Raw.Should().Be(42);
        skills.Skills.Should().ContainKey("Archery");
        skills.Skills["Archery"].Raw.Should().Be(15);

        var inventory = sp.GetRequiredService<IInventoryState>();
        inventory.Items.Should().ContainKey(84741837);
        inventory.Items[84741837].InternalName.Should().Be("GoblinCap");

        var weather = sp.GetRequiredService<IWeatherState>();
        weather.CurrentWeather.Should().Be("Clear");
    }

    /// <summary>
    /// A zone transition resets area-scoped state (position, weather, NPC) but
    /// preserves cross-area state (inventory, skills). The new area and spawn
    /// position are established from the second zone's log events.
    /// </summary>
    [Fact]
    public async Task ZoneTransition_ResetsAreaScopedState_PreservesInventory()
    {
        var lines = new[]
        {
            "[14:30:01] LOADING LEVEL AreaSerbule",
            "[14:30:02] !!! Initializing area! (502934): AreaSerbule",
            "[14:30:03] LocalPlayer: ProcessAddPlayer(-1, 25042203, \"@m\", \"TestChar\", \"d\", System.String[], (100, 200, 300), (0, 0, 0, 0), Idle, Standing, 0, 0, True)",
            "[14:30:04] LocalPlayer: ProcessAddItem(Sword(11111), -1, False)",
            "[14:30:05] LocalPlayer: ProcessSetWeather(\"Foggy\", True)",
            "[14:30:10] LOADING LEVEL AreaKurCaves",
            "[14:30:11] !!! Initializing area! (603841): AreaKurCaves",
            "[14:30:12] LocalPlayer: ProcessAddPlayer(-2, 25042204, \"@m\", \"TestChar\", \"d\", System.String[], (50, 75, 25), (0, 0, 0, 0), Idle, Standing, 0, 0, True)",
        };

        var (sp, driver) = BuildPipeline(lines);
        await driver.RunAsync(CancellationToken.None);

        var area = sp.GetRequiredService<IAreaState>();
        area.CurrentArea.Should().Be("AreaKurCaves");

        var position = sp.GetRequiredService<IPositionState>();
        position.X.Should().Be(50);
        position.Y.Should().Be(75);
        position.Z.Should().Be(25);

        var weather = sp.GetRequiredService<IWeatherState>();
        weather.CurrentWeather.Should().BeNull("weather resets on zone transition");

        var inventory = sp.GetRequiredService<IInventoryState>();
        inventory.Items.Should().ContainKey(11111, "inventory persists across zones");
    }

    private static (IServiceProvider SP, WorldDriver Driver) BuildPipeline(string[] logLines)
    {
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance)
            .AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        var bus = new DomainEventBus(NullLogger<DomainEventBus>.Instance);
        services.AddSingleton(bus);
        services.AddSingleton<IDomainEventSubscriber>(bus);
        services.AddSingleton<IDomainEventPublisher>(bus);

        var builder = new ArdaBuilder(services);
        builder.AddPlayerWorld();
        var sp = services.BuildServiceProvider();

        var table = builder.BuildDispatchTable(sp);
        var observers = builder.BuildLineObservers(sp);
        var source = new FiniteLogSource(logLines);
        var driver = new WorldDriver(source, table, observers: observers);

        return (sp, driver);
    }

    private sealed class FiniteLogSource(string[] rawLines) : ILogLineSource
    {
        private const int TimestampPrefixLength = 11;

        public async IAsyncEnumerable<LogLine> Lines(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var now = DateTimeOffset.UtcNow;
            await Task.Yield();

            foreach (var raw in rawLines)
            {
                var trimmed = raw.TrimStart();
                if (trimmed.Length < TimestampPrefixLength) continue;
                if (trimmed[0] != '[' || trimmed[3] != ':' || trimmed[6] != ':' || trimmed[9] != ']')
                    continue;

                if (!int.TryParse(trimmed.AsSpan(1, 2), out var h) ||
                    !int.TryParse(trimmed.AsSpan(4, 2), out var m) ||
                    !int.TryParse(trimmed.AsSpan(7, 2), out var s))
                    continue;

                var timestamp = new DateTimeOffset(
                    now.Year, now.Month, now.Day, h, m, s, TimeSpan.Zero);
                var stripped = trimmed[TimestampPrefixLength..];
                var metadata = new LogLineMetadata(timestamp, now, IsReplay: true);
                yield return new LogLine(stripped, metadata);
            }
        }
    }
}
