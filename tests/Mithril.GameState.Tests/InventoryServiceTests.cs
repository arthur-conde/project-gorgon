using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using System.IO;
using FluentAssertions;
using Mithril.GameState.Inventory;
using Mithril.GameState.Tests.TestSupport;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Game;
using Mithril.Shared.Logging;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.GameState.Tests;

public sealed class InventoryServiceTests
{
    [Fact]
    public async Task ObservesProcessAddItem_PopulatesMap()
    {
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)",
            "[00:00:02] LocalPlayer: ProcessAddItem(AppleJuice(99), -1, False)");
        var svc = new InventoryService(stream.Driver);
        await RunUntilDrainedAsync(svc, stream);

        svc.TryResolve(42, out var a).Should().BeTrue();
        a.Should().Be("Moonstone");
        svc.TryResolve(99, out var b).Should().BeTrue();
        b.Should().Be("AppleJuice");
        svc.TryResolve(123, out _).Should().BeFalse();
    }

    [Fact]
    public async Task ProcessDeleteItem_KeepsEntryForLateConsumers()
    {
        // Two independent subscribers read the same stream at different paces. The
        // gift-detection path in Arwen calls TryResolve AFTER the inventory service
        // has already seen the delete — entries must remain queryable to avoid the
        // race that originally dropped calibration observations.
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)",
            "[00:00:02] LocalPlayer: ProcessDeleteItem(42)");
        var svc = new InventoryService(stream.Driver);
        await RunUntilDrainedAsync(svc, stream);

        svc.TryResolve(42, out var name).Should().BeTrue();
        name.Should().Be("Moonstone");
    }

    [Fact]
    public async Task LiveSubscriber_ReceivesAddAndDeleteInOrder()
    {
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)",
            "[00:00:02] LocalPlayer: ProcessDeleteItem(42)");
        var svc = new InventoryService(stream.Driver);
        var events = new List<InventoryEvent>();
        using var sub = svc.Subscribe(events.Add);

        await RunUntilDrainedAsync(svc, stream);

        events.Should().HaveCount(2);
        events[0].Kind.Should().Be(InventoryEventKind.Added);
        events[0].InstanceId.Should().Be(42);
        events[0].InternalName.Should().Be("Moonstone");
        events[1].Kind.Should().Be(InventoryEventKind.Deleted);
        events[1].InstanceId.Should().Be(42);
        events[1].InternalName.Should().Be("Moonstone");
    }

    [Fact]
    public async Task IgnoresUnknownProcessDeleteItem()
    {
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessDeleteItem(999)");
        var svc = new InventoryService(stream.Driver);
        var deleted = new List<InventoryEvent>();
        using var sub = svc.Subscribe(e => { if (e.Kind == InventoryEventKind.Deleted) deleted.Add(e); });

        await RunUntilDrainedAsync(svc, stream);

        deleted.Should().BeEmpty();
        svc.TryResolve(999, out _).Should().BeFalse();
    }

    [Fact]
    public async Task SubscribeAfterAdd_ReplaysFullEventLog()
    {
        // The late-subscribe race that #585 closed: a consumer attaching after
        // session-replay AddItem events have already been processed must see
        // those Added events, not be told inventory is empty. Per the React-
        // channel contract Subscribe now replays the full event log in order.
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)",
            "[00:00:02] LocalPlayer: ProcessAddItem(BarleySeeds(7), -1, True)");
        var svc = new InventoryService(stream.Driver);
        await RunUntilDrainedAsync(svc, stream);

        var replayed = new List<InventoryEvent>();
        using var sub = svc.Subscribe(replayed.Add);

        replayed.Should().HaveCount(2);
        replayed.Should().AllSatisfy(e => e.Kind.Should().Be(InventoryEventKind.Added));
        // Event-log replay preserves the original Fire order — a late subscriber
        // observes the same sequence an upfront subscriber would have.
        replayed.Select(e => (e.InstanceId, e.InternalName)).Should().Equal(new[]
        {
            (42L, "Moonstone"),
            (7L, "BarleySeeds"),
        });
    }

    [Fact]
    public async Task SubscribeAfterDelete_ReplaysAddThenDelete()
    {
        // #585: under the React-channel contract, a brand-new subscriber sees the
        // full event log, including Deleted events for items that were
        // added-and-deleted before it attached. Pre-#585 Subscribe silently
        // dropped these (only replayed live items as Added), which was the
        // bug class flagged by audits #579/#588 — Legolas/Motherlode would
        // miss dig-completion signals from before Mithril attached, Arwen
        // would miss gifts made in the same session-replay window, etc.
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)",
            "[00:00:02] LocalPlayer: ProcessDeleteItem(42)");
        var svc = new InventoryService(stream.Driver);
        await RunUntilDrainedAsync(svc, stream);

        var replayed = new List<InventoryEvent>();
        using var sub = svc.Subscribe(replayed.Add);

        replayed.Should().HaveCount(2);
        replayed[0].Kind.Should().Be(InventoryEventKind.Added);
        replayed[0].InstanceId.Should().Be(42);
        replayed[0].InternalName.Should().Be("Moonstone");
        replayed[1].Kind.Should().Be(InventoryEventKind.Deleted);
        replayed[1].InstanceId.Should().Be(42);
        replayed[1].InternalName.Should().Be("Moonstone");
        // TryResolve still returns the name (Arwen's gift-attribution path).
        svc.TryResolve(42, out var name).Should().BeTrue();
        name.Should().Be("Moonstone");
    }

    [Fact]
    public async Task SubscribeReplay_PreservesOriginalTimestamps()
    {
        // Samwise's plant-resolve window is 500 ms off SetPetOwner — replay
        // events with synthetic "now" timestamps would pass the window even
        // for items added an hour ago, breaking the correlation.
        var ts1 = new DateTime(2026, 4, 25, 10, 0, 0, DateTimeKind.Utc);
        var ts2 = new DateTime(2026, 4, 25, 10, 0, 1, DateTimeKind.Utc);
        var stream = new ScriptedStream(
            new RawLogLine(ts1, "[10:00:00] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)"),
            new RawLogLine(ts2, "[10:00:01] LocalPlayer: ProcessAddItem(BarleySeeds(7), -1, True)"));
        var svc = new InventoryService(stream.Driver);
        await RunUntilDrainedAsync(svc, stream);

        var replayed = new List<InventoryEvent>();
        using var sub = svc.Subscribe(replayed.Add);

        replayed.Should().HaveCount(2);
        replayed.Single(e => e.InstanceId == 42).Timestamp.Should().Be(ts1);
        replayed.Single(e => e.InstanceId == 7).Timestamp.Should().Be(ts2);
    }

    [Fact]
    public async Task DisposeSubscription_StopsFurtherEvents()
    {
        var stream = new ScriptedStream(Array.Empty<string>());
        var svc = new InventoryService(stream.Driver);
        var runTask = svc.StartAsync(CancellationToken.None);

        var events = new List<InventoryEvent>();
        var sub = svc.Subscribe(events.Add);

        stream.Push("[00:00:01] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));
        events.Should().HaveCount(1);

        sub.Dispose();
        sub.Dispose(); // idempotent

        stream.Push("[00:00:02] LocalPlayer: ProcessAddItem(AppleJuice(99), -1, False)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));
        events.Should().HaveCount(1, "the disposed subscription must not receive further events");

        await svc.StopAsync(CancellationToken.None);
        _ = runTask;
    }

    [Fact]
    public async Task SubscribeReplay_DeliversAddThenStackChanged()
    {
        // #585: the React-channel replay delivers the actual event log, not a
        // synthesized "current state" snapshot. A late subscriber sees the
        // Added (size 1) and the subsequent StackChanged (size 4) in order —
        // same shape an upfront subscriber would have observed. Consumers that
        // want "what's in inventory right now?" go through the Query channel
        // (TryGetStackSize), not the React channel.
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Guava(100), -1, True)",
            "[00:00:02] LocalPlayer: ProcessUpdateItemCode(100, 201920, True)"); // size 4
        var svc = new InventoryService(stream.Driver);
        await RunUntilDrainedAsync(svc, stream);

        var replayed = new List<InventoryEvent>();
        using var sub = svc.Subscribe(replayed.Add);

        replayed.Should().HaveCount(2);
        replayed[0].Kind.Should().Be(InventoryEventKind.Added);
        replayed[0].InstanceId.Should().Be(100);
        replayed[0].StackSize.Should().Be(1);
        replayed[1].Kind.Should().Be(InventoryEventKind.StackChanged);
        replayed[1].InstanceId.Should().Be(100);
        replayed[1].StackSize.Should().Be(4);
        // Query channel reports the post-mutation size.
        svc.TryGetStackSize(100, out var size).Should().BeTrue();
        size.Should().Be(4);
    }

    [Fact]
    public async Task UpdateItemCode_FiresStackChangedEventWithNewSize()
    {
        var stream = new ScriptedStream(Array.Empty<string>());
        var svc = new InventoryService(stream.Driver);
        var runTask = svc.StartAsync(CancellationToken.None);

        var events = new List<InventoryEvent>();
        using var sub = svc.Subscribe(events.Add);

        stream.Push("[00:00:01] LocalPlayer: ProcessAddItem(Guava(100), -1, True)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));
        stream.Push("[00:00:02] LocalPlayer: ProcessUpdateItemCode(100, 201920, True)"); // size 4
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

        events.Should().HaveCount(2);
        events[0].Kind.Should().Be(InventoryEventKind.Added);
        events[0].StackSize.Should().Be(1);
        events[1].Kind.Should().Be(InventoryEventKind.StackChanged);
        events[1].InstanceId.Should().Be(100);
        events[1].InternalName.Should().Be("Guava");
        events[1].StackSize.Should().Be(4);

        await svc.StopAsync(CancellationToken.None);
        _ = runTask;
    }

    [Fact]
    public async Task UpdateItemCode_TrueNoOp_DoesNotFire()
    {
        // A true no-op UpdateItemCode (size AND confirmation status both
        // unchanged) shouldn't push noise to subscribers. Note: the first
        // UpdateItemCode after a default-1 AddItem is *not* a no-op — it
        // promotes the entry from unconfirmed → confirmed, even when the
        // numeric size matches. So set up a confirmed entry first, then
        // verify a literal repeat is suppressed.
        var stream = new ScriptedStream(Array.Empty<string>());
        var svc = new InventoryService(stream.Driver);
        var runTask = svc.StartAsync(CancellationToken.None);

        var events = new List<InventoryEvent>();
        using var sub = svc.Subscribe(events.Add);

        stream.Push("[00:00:01] LocalPlayer: ProcessAddItem(Guava(100), -1, True)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));
        // First UpdateCode confirms size=4. Fires StackChanged (1 → confirmed 4).
        stream.Push("[00:00:02] LocalPlayer: ProcessUpdateItemCode(100, 201920, True)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));
        events.Should().HaveCount(2);
        events[1].Kind.Should().Be(InventoryEventKind.StackChanged);

        // Repeat the same code — both size and confirmation match. Suppressed.
        stream.Push("[00:00:03] LocalPlayer: ProcessUpdateItemCode(100, 201920, True)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

        events.Should().HaveCount(2, "repeat UpdateCode at the same size on a confirmed entry is a no-op");

        await svc.StopAsync(CancellationToken.None);
        _ = runTask;
    }

    [Fact]
    public async Task UpdateItemCode_FlipsUnconfirmedToConfirmed_EvenAtSameSize()
    {
        // The carryover-fix contract: an UpdateItemCode after a default-1 AddItem
        // must promote the entry from unconfirmed → confirmed and fire StackChanged
        // so subscribers know the size is now trustworthy — even when the decoded
        // size happens to be 1.
        var stream = new ScriptedStream(Array.Empty<string>());
        var svc = new InventoryService(stream.Driver);
        var runTask = svc.StartAsync(CancellationToken.None);

        var events = new List<InventoryEvent>();
        using var sub = svc.Subscribe(events.Add);

        stream.Push("[00:00:01] LocalPlayer: ProcessAddItem(Guava(100), -1, True)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));
        svc.TryGetStackSize(100, out _).Should().BeFalse();

        // code 0 → size 1, identical numeric to the default — but the bool flips.
        stream.Push("[00:00:02] LocalPlayer: ProcessUpdateItemCode(100, 0, True)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

        events.Should().HaveCount(2);
        events[1].Kind.Should().Be(InventoryEventKind.StackChanged);
        events[1].StackSize.Should().Be(1);
        svc.TryGetStackSize(100, out var size).Should().BeTrue();
        size.Should().Be(1);

        await svc.StopAsync(CancellationToken.None);
        _ = runTask;
    }

    [Fact]
    public async Task RemoveFromStorageVault_FiresStackChangedWithLiteralSize()
    {
        var stream = new ScriptedStream(Array.Empty<string>());
        var svc = new InventoryService(stream.Driver);
        var runTask = svc.StartAsync(CancellationToken.None);

        var events = new List<InventoryEvent>();
        using var sub = svc.Subscribe(events.Add);

        stream.Push("[00:00:01] LocalPlayer: ProcessAddItem(Guava(100), 116, True)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));
        stream.Push("[00:00:01] LocalPlayer: ProcessRemoveFromStorageVault(-131, -1, 100, 46)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

        events.Where(e => e.Kind == InventoryEventKind.StackChanged)
              .Should().ContainSingle()
              .Which.StackSize.Should().Be(46);

        await svc.StopAsync(CancellationToken.None);
        _ = runTask;
    }

    [Fact]
    public async Task DeleteEvent_CarriesLastKnownStackSize()
    {
        // Arwen's gift-attribution path needs the pre-delete size in the Deleted
        // event (or via TryGetStackSize after) — surface it on the event so
        // event-driven views (Palantir) get parity.
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(GiantSkull(100), -1, True)",
            "[00:00:02] LocalPlayer: ProcessUpdateItemCode(100, 3211264, True)", // size 50
            "[00:00:03] LocalPlayer: ProcessDeleteItem(100)");
        var svc = new InventoryService(stream.Driver);
        var events = new List<InventoryEvent>();
        using var sub = svc.Subscribe(events.Add);

        await RunUntilDrainedAsync(svc, stream);

        var del = events.Single(e => e.Kind == InventoryEventKind.Deleted);
        del.InstanceId.Should().Be(100);
        del.StackSize.Should().Be(50);
    }

    [Fact]
    public async Task StrandedPendingAdd_IsEvictedByPiggybackDrain()
    {
        // Pin: a ProcessAddItem that never finds a matching chat status used to
        // sit in _pendingAdd[InternalName] forever, because the queue's TTL was
        // only consulted on the matching path. Now every Handle* method calls
        // DrainPendingStale at entry, so the next unrelated event evicts it.
        var time = new ManualTimeProvider(new DateTime(2026, 4, 25, 14, 0, 0, DateTimeKind.Utc));
        var stream = new ScriptedStream(Array.Empty<string>());
        var svc = new InventoryService(stream.Driver, time: time);
        var runTask = svc.StartAsync(CancellationToken.None);

        // 1) Drive an AddItem with no chat correlation → enqueues _pendingAdd["Moonstone"].
        stream.Push("[00:00:01] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));
        svc.PendingCounts().Add.Should().Be(1, "Moonstone AddItem with no chat must enqueue pending-add");

        // 2) Advance past the 5-second TTL.
        time.Advance(TimeSpan.FromSeconds(10));

        // 3) Drive an unrelated event — its DrainPendingStale must evict the stranded entry.
        stream.Push("[00:00:11] LocalPlayer: ProcessAddItem(Guava(99), -1, True)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

        // 4) Moonstone's stranded pending-add is gone; Guava's is the only one left.
        svc.PendingCounts().Add.Should().Be(1, "stranded Moonstone evicted; Guava's still fresh");
        svc.PendingCounts().Chat.Should().Be(0);

        await svc.StopAsync(CancellationToken.None);
        _ = runTask;
    }

    [Fact]
    public async Task ExportSeed_StackableSingleStack_AddItemUsesSeededSize()
    {
        // Pin: when Mithril restarts mid-PG-session, _map is empty and a session-replay
        // AddItem would otherwise default to size = 1. The newest *_items_*.json export
        // carries the authoritative size; HandleAddItem consults _seededStackSizes
        // before falling back to the default.
        using var fixture = new SeedFixture();
        fixture.WriteExport("""
{
  "Character": "Hits",
  "ServerName": "Pluto",
  "Timestamp": "2026-04-25T14:00:00Z",
  "Report": "items",
  "ReportVersion": 1,
  "Items": [
    { "TypeID": 10251, "Name": "Barley Seeds", "StackSize": 23, "Value": 0, "IsInInventory": true, "IsCrafted": false }
  ]
}
""");
        var stream = new ScriptedStream(Array.Empty<string>());
        var svc = new InventoryService(stream.Driver, refData: fixture.RefData, gameConfig: fixture.GameConfig);
        var runTask = svc.StartAsync(CancellationToken.None);

        var events = new List<InventoryEvent>();
        using var sub = svc.Subscribe(events.Add);

        stream.Push("[00:00:01] LocalPlayer: ProcessAddItem(BarleySeeds(42), -1, True)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

        events.Should().ContainSingle();
        events[0].Kind.Should().Be(InventoryEventKind.Added);
        events[0].InstanceId.Should().Be(42);
        events[0].StackSize.Should().Be(23, "the seed from the export must beat the size = 1 default");

        await svc.StopAsync(CancellationToken.None);
        _ = runTask;
    }

    [Fact]
    public async Task ExportSeed_ConsumedOnFirstHit_SecondAddDefaultsToOne()
    {
        // The seed represents "the export said there's exactly one stack of N." Once
        // consumed, a second AddItem of the same InternalName must NOT inherit it —
        // otherwise we'd over-claim sizes for items the player picked up in-session.
        using var fixture = new SeedFixture();
        fixture.WriteExport("""
{
  "Character": "Hits",
  "ServerName": "Pluto",
  "Timestamp": "2026-04-25T14:00:00Z",
  "Report": "items",
  "ReportVersion": 1,
  "Items": [
    { "TypeID": 10251, "Name": "Barley Seeds", "StackSize": 23, "Value": 0, "IsInInventory": true, "IsCrafted": false }
  ]
}
""");
        var stream = new ScriptedStream(Array.Empty<string>());
        var svc = new InventoryService(stream.Driver, refData: fixture.RefData, gameConfig: fixture.GameConfig);
        var runTask = svc.StartAsync(CancellationToken.None);

        var events = new List<InventoryEvent>();
        using var sub = svc.Subscribe(events.Add);

        stream.Push("[00:00:01] LocalPlayer: ProcessAddItem(BarleySeeds(42), -1, True)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));
        stream.Push("[00:00:02] LocalPlayer: ProcessAddItem(BarleySeeds(99), -1, True)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

        events.Should().HaveCount(2);
        events[0].StackSize.Should().Be(23, "first add consumes the seed");
        events[1].StackSize.Should().Be(1, "second add must fall through to the default — seed already consumed");

        await svc.StopAsync(CancellationToken.None);
        _ = runTask;
    }

    [Fact]
    public async Task ExportSeed_AmbiguousMultipleStacks_NotSeeded()
    {
        // If the export shows multiple bag-stacks of the same InternalName, we have
        // no way to know which AddItem corresponds to which stack — so we drop the
        // entry entirely and let the default-1 path apply. This protects against
        // wildly wrong sizes for items the player is actively splitting/merging.
        using var fixture = new SeedFixture();
        fixture.WriteExport("""
{
  "Character": "Hits",
  "ServerName": "Pluto",
  "Timestamp": "2026-04-25T14:00:00Z",
  "Report": "items",
  "ReportVersion": 1,
  "Items": [
    { "TypeID": 10251, "Name": "Barley Seeds", "StackSize": 23, "Value": 0, "IsInInventory": true, "IsCrafted": false },
    { "TypeID": 10251, "Name": "Barley Seeds", "StackSize": 7,  "Value": 0, "IsInInventory": true, "IsCrafted": false }
  ]
}
""");
        var stream = new ScriptedStream(Array.Empty<string>());
        var svc = new InventoryService(stream.Driver, refData: fixture.RefData, gameConfig: fixture.GameConfig);
        var runTask = svc.StartAsync(CancellationToken.None);

        var events = new List<InventoryEvent>();
        using var sub = svc.Subscribe(events.Add);

        stream.Push("[00:00:01] LocalPlayer: ProcessAddItem(BarleySeeds(42), -1, True)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

        events.Should().ContainSingle();
        events[0].StackSize.Should().Be(1, "ambiguous multi-stack export must not seed any single InternalName");

        await svc.StopAsync(CancellationToken.None);
        _ = runTask;
    }

    [Fact]
    public async Task ExportReconcile_UpdatesSingleLiveEntry_FiresStackChanged()
    {
        // Mid-session export landing for an InternalName already tracked once in _map
        // (with a stale or default size) should reconcile the live entry to the
        // export's authoritative size and fire StackChanged so subscribers see it.
        using var fixture = new SeedFixture();
        var stream = new ScriptedStream(Array.Empty<string>());
        var svc = new InventoryService(stream.Driver, refData: fixture.RefData, gameConfig: fixture.GameConfig);
        var runTask = svc.StartAsync(CancellationToken.None);

        var events = new List<InventoryEvent>();
        using var sub = svc.Subscribe(events.Add);

        // No export at startup → AddItem defaults to size 1.
        stream.Push("[00:00:01] LocalPlayer: ProcessAddItem(BarleySeeds(42), -1, True)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));
        events.Should().ContainSingle();
        events[0].StackSize.Should().Be(1);

        // Now an export lands carrying StackSize=23 for the same InternalName.
        // Trigger LoadExportSeeds directly (the FSW path is asynchronous and
        // would race the assertions).
        fixture.WriteExport("""
{
  "Character": "Hits",
  "ServerName": "Pluto",
  "Timestamp": "2026-04-25T14:00:00Z",
  "Report": "items",
  "ReportVersion": 1,
  "Items": [
    { "TypeID": 10251, "Name": "Barley Seeds", "StackSize": 23, "Value": 0, "IsInInventory": true, "IsCrafted": false }
  ]
}
""");
        svc.LoadExportSeeds();

        events.Should().HaveCount(2, "the reconcile pass must fire StackChanged for the corrected entry");
        events[1].Kind.Should().Be(InventoryEventKind.StackChanged);
        events[1].InstanceId.Should().Be(42);
        events[1].StackSize.Should().Be(23);

        svc.TryGetStackSize(42, out var size).Should().BeTrue();
        size.Should().Be(23);

        await svc.StopAsync(CancellationToken.None);
        _ = runTask;
    }

    [Fact]
    public async Task ExportReconcile_AmbiguousLiveDuplicates_LeavesEntriesAlone()
    {
        // If the live map already holds two entries of the same InternalName, we
        // can't tell which one the export's size belongs to — leave both untouched
        // and just refresh _seededStackSizes for future AddItems.
        using var fixture = new SeedFixture();
        var stream = new ScriptedStream(Array.Empty<string>());
        var svc = new InventoryService(stream.Driver, refData: fixture.RefData, gameConfig: fixture.GameConfig);
        var runTask = svc.StartAsync(CancellationToken.None);

        var events = new List<InventoryEvent>();
        using var sub = svc.Subscribe(events.Add);

        stream.Push("[00:00:01] LocalPlayer: ProcessAddItem(BarleySeeds(42), -1, True)");
        stream.Push("[00:00:02] LocalPlayer: ProcessAddItem(BarleySeeds(99), -1, True)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));
        events.Should().HaveCount(2);

        // Even though the export shows a single Barley stack of size 23, we have
        // TWO live BarleySeeds entries — ambiguous, no reconcile.
        fixture.WriteExport("""
{
  "Character": "Hits",
  "ServerName": "Pluto",
  "Timestamp": "2026-04-25T14:00:00Z",
  "Report": "items",
  "ReportVersion": 1,
  "Items": [
    { "TypeID": 10251, "Name": "Barley Seeds", "StackSize": 23, "Value": 0, "IsInInventory": true, "IsCrafted": false }
  ]
}
""");
        svc.LoadExportSeeds();

        events.Should().HaveCount(2, "ambiguous live duplicates → no StackChanged fires");
        // Both entries remain unconfirmed (default-1 from AddItem with no chat),
        // and the ambiguous reconcile correctly refused to bless either with the
        // export's size — so TryGetStackSize still reports unknown for both.
        svc.TryGetStackSize(42, out _).Should().BeFalse();
        svc.TryGetStackSize(99, out _).Should().BeFalse();

        await svc.StopAsync(CancellationToken.None);
        _ = runTask;
    }

    [Fact]
    public async Task ExportReconcile_NoSizeChange_DoesNotFire()
    {
        // Reconcile is a no-op when the live entry already matches the export.
        using var fixture = new SeedFixture();
        fixture.WriteExport("""
{
  "Character": "Hits",
  "ServerName": "Pluto",
  "Timestamp": "2026-04-25T14:00:00Z",
  "Report": "items",
  "ReportVersion": 1,
  "Items": [
    { "TypeID": 10251, "Name": "Barley Seeds", "StackSize": 23, "Value": 0, "IsInInventory": true, "IsCrafted": false }
  ]
}
""");
        var stream = new ScriptedStream(Array.Empty<string>());
        var svc = new InventoryService(stream.Driver, refData: fixture.RefData, gameConfig: fixture.GameConfig);
        var runTask = svc.StartAsync(CancellationToken.None);

        var events = new List<InventoryEvent>();
        using var sub = svc.Subscribe(events.Add);

        // AddItem consumes the seed → live entry already has size 23.
        stream.Push("[00:00:01] LocalPlayer: ProcessAddItem(BarleySeeds(42), -1, True)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));
        events.Should().ContainSingle();
        events[0].StackSize.Should().Be(23);

        // Fresh export with the same size → reconcile finds nothing to change.
        svc.LoadExportSeeds();

        events.Should().ContainSingle("no-op reconcile must not fire StackChanged");

        await svc.StopAsync(CancellationToken.None);
        _ = runTask;
    }

    // ---- #585 React-channel contract: full event-log replay ----------------

    [Fact]
    public async Task Subscribe_FullEventLogReplay_PreservesAddDeleteAddDeleteOrder()
    {
        // #585 acceptance test: a sequence of Add → Delete → Add → Delete fired
        // before subscription must replay in order. The pre-#585 contract
        // synthesized at most one Added per surviving live entry, so this
        // sequence would have replayed as zero events (both items deleted).
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)",
            "[00:00:02] LocalPlayer: ProcessDeleteItem(42)",
            "[00:00:03] LocalPlayer: ProcessAddItem(BarleySeeds(7), -1, True)",
            "[00:00:04] LocalPlayer: ProcessDeleteItem(7)");
        var svc = new InventoryService(stream.Driver);
        await RunUntilDrainedAsync(svc, stream);

        var replayed = new List<InventoryEvent>();
        using var sub = svc.Subscribe(replayed.Add);

        replayed.Should().HaveCount(4);
        replayed.Select(e => (e.Kind, e.InstanceId)).Should().Equal(new[]
        {
            (InventoryEventKind.Added, 42L),
            (InventoryEventKind.Deleted, 42L),
            (InventoryEventKind.Added, 7L),
            (InventoryEventKind.Deleted, 7L),
        });
    }

    [Fact]
    public async Task Subscribe_LiveOnly_SkipsBacklogAndDeliversSubsequentEvents()
    {
        // Opt-out path for consumers that genuinely don't care about
        // pre-attach history. Replay is skipped; only events fired AFTER
        // Subscribe is established are delivered.
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)",
            "[00:00:02] LocalPlayer: ProcessDeleteItem(42)");
        var svc = new InventoryService(stream.Driver);
        var runTask = svc.StartAsync(CancellationToken.None);
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(5));

        var events = new List<InventoryEvent>();
        using var sub = svc.Subscribe(events.Add, ReplayMode.LiveOnly);
        events.Should().BeEmpty("LiveOnly skips the session backlog");

        stream.Push("[00:00:03] LocalPlayer: ProcessAddItem(BarleySeeds(7), -1, True)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

        events.Should().ContainSingle();
        events[0].Kind.Should().Be(InventoryEventKind.Added);
        events[0].InstanceId.Should().Be(7);

        await svc.StopAsync(CancellationToken.None);
        _ = runTask;
    }

    [Fact]
    public async Task Subscribe_DefaultReplayMode_IsFromSessionStart()
    {
        // The zero-arg overload must default to full-log replay — this is the
        // contract every existing in-tree consumer (Samwise, Palantir,
        // Legolas/Motherlode) relies on after #585.
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)",
            "[00:00:02] LocalPlayer: ProcessDeleteItem(42)");
        var svc = new InventoryService(stream.Driver);
        await RunUntilDrainedAsync(svc, stream);

        var events = new List<InventoryEvent>();
        using var sub = svc.Subscribe(events.Add); // no ReplayMode arg

        events.Should().HaveCount(2);
        events[0].Kind.Should().Be(InventoryEventKind.Added);
        events[1].Kind.Should().Be(InventoryEventKind.Deleted);
    }

    [Fact]
    public async Task Subscribe_AtomicReplayThenLive_NoDuplicateOrLostEvents()
    {
        // The lock-around-Fire-and-Append discipline must hold: an event fired
        // after Subscribe returns is delivered exactly once (live), not also
        // re-delivered as part of a stale replay snapshot, and not missed
        // because the replay snapshot was taken before append.
        //
        // Drive backlog → subscribe → fire a live event under the same drain
        // gate. The subscriber should see exactly: backlog Added + live Added.
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)");
        var svc = new InventoryService(stream.Driver);
        var runTask = svc.StartAsync(CancellationToken.None);
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(5));

        var events = new List<InventoryEvent>();
        using var sub = svc.Subscribe(events.Add);
        events.Should().ContainSingle("backlog Moonstone Added replays atomically");

        stream.Push("[00:00:02] LocalPlayer: ProcessAddItem(BarleySeeds(7), -1, True)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

        events.Should().HaveCount(2,
            "live event is delivered exactly once via the live-handler path, not duplicated through replay");
        events[0].InstanceId.Should().Be(42);
        events[1].InstanceId.Should().Be(7);

        await svc.StopAsync(CancellationToken.None);
        _ = runTask;
    }

    [Fact]
    public async Task LateSubscriber_ReceivesDeletedEventsSynthesizedBeforeAttach()
    {
        // The cross-pump-race scenario from #585: a Motherlode-style consumer
        // attaches AFTER the L1 driver has already pumped session-replay
        // AddItem + DeleteItem pairs through InventoryService. The pre-#585
        // contract dropped those Deleted events entirely (the live map was
        // empty by the time Subscribe ran). The new contract replays them
        // from the event log so the consumer's Deleted-handler (the
        // dig-completion signal for Legolas, the gift-attribution signal
        // for Arwen) fires correctly.
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(MotherlodeMap_Mine(42), -1, True)",
            "[00:00:02] LocalPlayer: ProcessDeleteItem(42)",
            "[00:00:03] LocalPlayer: ProcessAddItem(MotherlodeMap_Mine(43), -1, True)",
            "[00:00:04] LocalPlayer: ProcessDeleteItem(43)");
        var svc = new InventoryService(stream.Driver);
        await RunUntilDrainedAsync(svc, stream);

        // Late subscriber that only cares about deletes (Motherlode pattern).
        var deletes = new List<InventoryEvent>();
        using var sub = svc.Subscribe(e =>
        {
            if (e.Kind == InventoryEventKind.Deleted) deletes.Add(e);
        });

        deletes.Should().HaveCount(2,
            "both dig-completion signals must reach the late subscriber even though both maps were already consumed before attach");
        deletes.Select(e => e.InstanceId).Should().Equal(42L, 43L);
    }

    [Fact]
    public async Task EventLog_ExceedsSoftCap_DropsOldestAndWarnsOnce()
    {
        // Pathological session: a long-running Mithril instance accumulates
        // tens of thousands of inventory events. The log soft-caps at
        // EventLogSoftCap (50,000) by dropping oldest entries in chunks; a
        // single Warn fires the first time the cap is exceeded so the
        // truncation isn't silent. The trim chunk is large (4096) so the
        // log oscillates between cap − chunk and cap, not at cap exactly.
        const int over = 60_000;
        var stream = new ScriptedStream(Array.Empty<string>());
        var diag = new RecordingDiagnostics();
        var svc = new InventoryService(stream.Driver, diag: diag);
        var runTask = svc.StartAsync(CancellationToken.None);

        for (int i = 0; i < over; i++)
            stream.Push($"[00:00:01] LocalPlayer: ProcessAddItem(BarleySeeds({i}), -1, True)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(30));

        // Late subscriber sees a bounded replay (the cap, not all 60k events).
        var replayed = new List<InventoryEvent>();
        using var sub = svc.Subscribe(replayed.Add);

        replayed.Count.Should().BeLessThan(over,
            "the event log must be soft-capped — late subscribers receive bounded history");
        replayed.Count.Should().BeLessThanOrEqualTo(50_000,
            "soft cap = 50_000; trim brings the log under-or-equal to cap");
        replayed.Count.Should().BeGreaterThan(40_000,
            "trim chunk is bounded so the log stays close to (not far below) the cap");

        // Exactly one overflow Warn, emitted the first time the cap was hit.
        var overflowWarns = diag.Warns
            .Where(w => w.Category == "GameState.Inventory" && w.Message.Contains("soft cap"))
            .ToList();
        overflowWarns.Should().ContainSingle(
            "the overflow Warn is one-shot — pathological sessions shouldn't spam diagnostics");

        await svc.StopAsync(CancellationToken.None);
        _ = runTask;
    }

    private sealed class RecordingDiagnostics : IDiagnosticsSink
    {
        public List<(string Category, string Message)> Warns { get; } = new();
        private readonly object _gate = new();
        private readonly List<DiagnosticEntry> _entries = new();

        public event EventHandler<DiagnosticEntry>? EntryAdded;

        public void Write(DiagnosticLevel level, string category, string message)
        {
            var entry = new DiagnosticEntry(DateTime.UtcNow, level, category, message);
            lock (_gate)
            {
                _entries.Add(entry);
                if (level == DiagnosticLevel.Warn) Warns.Add((category, message));
            }
            EntryAdded?.Invoke(this, entry);
        }

        public IReadOnlyList<DiagnosticEntry> Snapshot()
        {
            lock (_gate) return _entries.ToArray();
        }
    }

    [Fact]
    public async Task SubscriberException_DoesNotBreakOtherSubscribers()
    {
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)");
        var svc = new InventoryService(stream.Driver);

        var goodEvents = new List<InventoryEvent>();
        using var bad = svc.Subscribe(_ => throw new InvalidOperationException("boom"));
        using var good = svc.Subscribe(goodEvents.Add);

        await RunUntilDrainedAsync(svc, stream);

        goodEvents.Should().ContainSingle(e => e.InstanceId == 42 && e.Kind == InventoryEventKind.Added);
    }

    private static async Task RunUntilDrainedAsync(InventoryService svc, ScriptedStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);
        await cts.CancelAsync();
        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
        _ = runTask;
    }

    /// <summary>
    /// Stands up a temp <c>GameRoot/Reports/</c> directory and a stub
    /// <see cref="IReferenceDataService"/> that knows about Barley Seeds
    /// (TypeID 10251, MaxStackSize 100). Cleans up on dispose.
    /// Marked <c>internal</c> so sibling test files in the same assembly
    /// (e.g. <c>InventoryServiceStackSizeTests</c>) can reuse it.
    /// </summary>
    internal sealed class SeedFixture : IDisposable
    {
        public string GameRoot { get; }
        public string ReportsDir { get; }
        public GameConfig GameConfig { get; }
        public IReferenceDataService RefData { get; }

        public SeedFixture()
        {
            GameRoot = Path.Combine(Path.GetTempPath(), "MithrilSeedTests-" + Guid.NewGuid().ToString("N"));
            ReportsDir = Path.Combine(GameRoot, "Reports");
            Directory.CreateDirectory(ReportsDir);
            GameConfig = new GameConfig { GameRoot = GameRoot };
            RefData = new BarleyOnlyRefData();
        }

        public void WriteExport(string json)
        {
            // File name must match StorageReportLoader's filename regex: {Char}_{Server}_items_*
            var path = Path.Combine(ReportsDir, "Hits_Pluto_items_2026-04-25.json");
            File.WriteAllText(path, json);
        }

        public void Dispose()
        {
            try { Directory.Delete(GameRoot, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private sealed class BarleyOnlyRefData : IReferenceDataService
    {
        public IReadOnlyList<string> Keys { get; } = ["items"];
        private static readonly Item _barley = new()
        {
            Id = 10251, Name = "Barley Seeds", InternalName = "BarleySeeds",
            MaxStackSize = 100, IconId = 0, Keywords = [],
        };
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>
        {
            [10251L] = _barley,
        };
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>(StringComparer.Ordinal)
        {
            ["BarleySeeds"] = _barley,
        };
        public ItemKeywordIndex KeywordIndex => new(Items);
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> Quests { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public ReferenceFileSnapshot GetSnapshot(string key) => new("items", ReferenceFileSource.Bundled, "test", null, 1);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }

    /// <summary>
    /// Test-only TimeProvider whose clock advances only when the test calls
    /// <see cref="Advance"/>. Lets TTL-eviction tests run deterministically.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTimeProvider(DateTime utcStart) => _now = new DateTimeOffset(utcStart, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    /// <summary>
    /// Driver-backed test stream. Wraps <see cref="TestLogStreamDriver"/> and
    /// pre-strips Player.log shapes onto the LocalPlayer pipe via
    /// <see cref="TestLogEnvelopeFactory.FromRawLine(RawLogLine)"/> — matches
    /// the L0.5 router's envelope-strip behaviour so InventoryService's
    /// LocalPlayer subscription sees the same shape as in production. Ctor
    /// pushes lines as <em>replay</em> (so they're available before the
    /// subscription starts); <see cref="Push"/> pushes <em>live</em>.
    /// </summary>
    internal sealed class ScriptedStream : IDisposable
    {
        public TestLogStreamDriver Driver { get; } = new();

        public ScriptedStream(params string[] lines)
            : this(lines.Select(l => new RawLogLine(DateTime.UtcNow, l)).ToArray()) { }

        public ScriptedStream(params RawLogLine[] lines)
        {
            foreach (var line in lines)
                Driver.PushReplay(TestLogEnvelopeFactory.FromRawLine(line));
        }

        public void Push(string line) =>
            Driver.PushLive(TestLogEnvelopeFactory.FromRawLine(new RawLogLine(DateTime.UtcNow, line)));

        public void Push(RawLogLine line) =>
            Driver.PushLive(TestLogEnvelopeFactory.FromRawLine(line));

        public Task WaitForDrainAsync(CancellationToken ct) =>
            Driver.DrainLocalPlayerAsync().WaitAsync(ct);
        public Task WaitForDrainAsync(TimeSpan timeout) =>
            Driver.DrainLocalPlayerAsync(timeout);

        public void Dispose() => Driver.Dispose();
    }
}
