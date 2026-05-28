using FluentAssertions;
using Arda.Abstractions.Logs;
using Arda.World.Player.Events;
using Legolas.Domain;
using Legolas.Services;
using Legolas.Tests.TestSupport;
using Legolas.ViewModels;

namespace Legolas.Tests.Services;

/// <summary>
/// Post-#824 pivot to ScreenText-as-truth: the tracker subscribes only to
/// <see cref="ScreenTextObserved"/> via <see cref="Arda.Contracts.IDomainEventSubscriber"/>
/// and credits the counts parsed straight out of the chat banner. The
/// <see cref="InventoryItemAdded"/> add-channel has been retired (it dropped
/// counts on stack-onto-existing and always emitted StackSize=1 on fresh
/// instances). Tests publish events through a <see cref="TestDomainEventBus"/>.
/// </summary>
public sealed class ItemCollectionTrackerTests
{
    private static readonly LogLineMetadata LiveMeta = new(
        Timestamp: new DateTimeOffset(new DateTime(2026, 5, 22, 14, 0, 0, DateTimeKind.Utc), TimeSpan.Zero),
        ReadOn: DateTimeOffset.UtcNow,
        IsReplay: false);

    private static readonly LogLineMetadata ReplayMeta = new(
        Timestamp: new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero),
        ReadOn: DateTimeOffset.UtcNow,
        IsReplay: true);

    private sealed record Harness(
        ItemCollectionTracker Service,
        TestDomainEventBus Bus,
        SessionState Session);

    private static Harness Build()
    {
        var bus = new TestDomainEventBus();
        var session = new SessionState();
        var svc = new ItemCollectionTracker(bus, session);
        return new Harness(svc, bus, session);
    }

    private static SurveyItemViewModel SeedSurvey(SessionState session, string displayName)
    {
        var vm = new SurveyItemViewModel(Survey.Create(displayName, new MetreOffset(0, 0), gridIndex: 0));
        session.Surveys.Add(vm);
        return vm;
    }

    private static async Task Run(Harness h, Func<Task> body)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await h.Service.StartAsync(cts.Token);
        try { await body(); }
        finally
        {
            await cts.CancelAsync();
            try { await h.Service.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { }
            h.Service.Dispose();
        }
    }

    // ---- basic collect flow ----------------------------------------------

    [Fact]
    public async Task Collect_credits_text_count_without_any_add()
    {
        // Post-#824: ScreenText is the source of truth. Even without any
        // InventoryItemAdded firing (the stack-onto-existing case from the
        // Player-2026-05-20-0400.log corpus), the collect line must credit
        // the slot.
        var h = Build();
        await Run(h, async () =>
        {
            var survey = SeedSurvey(h.Session, "Iron Ore");

            h.Bus.Publish(new ScreenTextObserved(
                "ImportantInfo".AsMemory(),
                "Iron Ore collected!".AsMemory(),
                LiveMeta));

            await Task.Yield();
            survey.Collected.Should().BeTrue();
            h.Session.CollectedItems.Should().ContainKey("Iron Ore")
                .WhoseValue.Should().Be(1);
        });
    }

    [Fact]
    public async Task Stack_onto_existing_credits_one_with_no_inventory_event()
    {
        // 02:02:36 in Player-2026-05-20-0400.log — "Rubywall Crystal collected!"
        // fires with no ProcessAddItem and no ProcessUpdateItemCode-derived
        // bus event the tracker subscribes to. ScreenText alone must credit 1.
        var h = Build();
        await Run(h, async () =>
        {
            SeedSurvey(h.Session, "Rubywall Crystal");

            h.Bus.Publish(new ScreenTextObserved(
                "ImportantInfo".AsMemory(),
                "Rubywall Crystal collected!".AsMemory(),
                LiveMeta));

            await Task.Yield();
            h.Session.CollectedItems.Should().ContainKey("Rubywall Crystal")
                .WhoseValue.Should().Be(1);
        });
    }

    [Fact]
    public async Task Speed_bonus_is_credited_separately()
    {
        // Post-#824: bonus count comes from the chat text's "xN" tail, NOT
        // from inventory subscribers. The Copper Ore x2 banner must credit 2,
        // not 1.
        var h = Build();
        await Run(h, async () =>
        {
            SeedSurvey(h.Session, "Iron Ore");

            h.Bus.Publish(new ScreenTextObserved(
                "ImportantInfo".AsMemory(),
                "Iron Ore collected! Also found Copper Ore x2 (speed bonus!)".AsMemory(),
                LiveMeta));

            await Task.Yield();
            h.Session.CollectedItems.Should().ContainKey("Iron Ore").WhoseValue.Should().Be(1);
            h.Session.CollectedItems.Should().ContainKey("Copper Ore").WhoseValue.Should().Be(2);
        });
    }

    [Fact]
    public async Task Replay_events_are_dropped()
    {
        var h = Build();
        await Run(h, async () =>
        {
            SeedSurvey(h.Session, "Iron Ore");

            h.Bus.Publish(new ScreenTextObserved(
                "ImportantInfo".AsMemory(),
                "Iron Ore collected!".AsMemory(),
                ReplayMeta));

            await Task.Yield();
            h.Session.CollectedItems.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task Non_ImportantInfo_category_is_ignored()
    {
        var h = Build();
        await Run(h, async () =>
        {
            SeedSurvey(h.Session, "Iron Ore");

            h.Bus.Publish(new ScreenTextObserved(
                "GeneralInfo".AsMemory(),
                "Iron Ore collected!".AsMemory(),
                LiveMeta));

            await Task.Yield();
            h.Session.CollectedItems.Should().BeEmpty();
        });
    }
}
