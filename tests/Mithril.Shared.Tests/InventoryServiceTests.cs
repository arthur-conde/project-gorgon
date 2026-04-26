using System.IO;
using System.Threading.Channels;
using FluentAssertions;
using Mithril.Shared.Game;
using Mithril.Shared.Inventory;
using Mithril.Shared.Logging;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests;

public sealed class InventoryServiceTests
{
    [Fact]
    public async Task ObservesProcessAddItem_PopulatesMap()
    {
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)",
            "[00:00:02] LocalPlayer: ProcessAddItem(AppleJuice(99), -1, False)");
        var svc = new InventoryService(stream);
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
        var svc = new InventoryService(stream);
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
        var svc = new InventoryService(stream);
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
        var svc = new InventoryService(stream);
        var deleted = new List<InventoryEvent>();
        using var sub = svc.Subscribe(e => { if (e.Kind == InventoryEventKind.Deleted) deleted.Add(e); });

        await RunUntilDrainedAsync(svc, stream);

        deleted.Should().BeEmpty();
        svc.TryResolve(999, out _).Should().BeFalse();
    }

    [Fact]
    public async Task SubscribeAfterAdd_ReplaysCurrentMapAsAddedEvents()
    {
        // The late-subscribe race that caused issue #7: the seed AddItem fires
        // during PlayerLogStream's session-replay flush, before the gated module
        // attaches its handler. Subscribe must replay the live map so the new
        // subscriber sees the same history as one that was attached upfront.
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)",
            "[00:00:02] LocalPlayer: ProcessAddItem(BarleySeeds(7), -1, True)");
        var svc = new InventoryService(stream);
        await RunUntilDrainedAsync(svc, stream);

        var replayed = new List<InventoryEvent>();
        using var sub = svc.Subscribe(replayed.Add);

        replayed.Should().HaveCount(2);
        replayed.Should().AllSatisfy(e => e.Kind.Should().Be(InventoryEventKind.Added));
        replayed.Select(e => (e.InstanceId, e.InternalName)).Should().BeEquivalentTo(new[]
        {
            (42L, "Moonstone"),
            (7L, "BarleySeeds"),
        });
    }

    [Fact]
    public async Task SubscribeAfterDelete_DoesNotReplayDeletedEntry()
    {
        // Deleted entries are retained for TryResolve, but a brand-new subscriber
        // shouldn't be told an item exists that's already gone. Otherwise Samwise
        // would treat a stale id as a candidate seed for the next plant.
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)",
            "[00:00:02] LocalPlayer: ProcessDeleteItem(42)");
        var svc = new InventoryService(stream);
        await RunUntilDrainedAsync(svc, stream);

        var replayed = new List<InventoryEvent>();
        using var sub = svc.Subscribe(replayed.Add);

        replayed.Should().BeEmpty();
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
        var svc = new InventoryService(stream);
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
        var svc = new InventoryService(stream);
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
    public async Task SubscribeReplay_CarriesCurrentStackSize()
    {
        // Once a stack has been mutated by UpdateItemCode, a fresh subscriber must
        // see the post-mutation size on the synthesized Added event so the view
        // doesn't render a stale "1" until the next live event.
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Guava(100), -1, True)",
            "[00:00:02] LocalPlayer: ProcessUpdateItemCode(100, 201920, True)"); // size 4
        var svc = new InventoryService(stream);
        await RunUntilDrainedAsync(svc, stream);

        var replayed = new List<InventoryEvent>();
        using var sub = svc.Subscribe(replayed.Add);

        replayed.Should().ContainSingle();
        replayed[0].Kind.Should().Be(InventoryEventKind.Added);
        replayed[0].InstanceId.Should().Be(100);
        replayed[0].StackSize.Should().Be(4);
    }

    [Fact]
    public async Task UpdateItemCode_FiresStackChangedEventWithNewSize()
    {
        var stream = new ScriptedStream(Array.Empty<string>());
        var svc = new InventoryService(stream);
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
        var svc = new InventoryService(stream);
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
        var svc = new InventoryService(stream);
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
        var svc = new InventoryService(stream);
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
        var svc = new InventoryService(stream);
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
        var svc = new InventoryService(stream, time: time);
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
        var svc = new InventoryService(stream, refData: fixture.RefData, gameConfig: fixture.GameConfig);
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
        var svc = new InventoryService(stream, refData: fixture.RefData, gameConfig: fixture.GameConfig);
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
        var svc = new InventoryService(stream, refData: fixture.RefData, gameConfig: fixture.GameConfig);
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
        var svc = new InventoryService(stream, refData: fixture.RefData, gameConfig: fixture.GameConfig);
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
        var svc = new InventoryService(stream, refData: fixture.RefData, gameConfig: fixture.GameConfig);
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
        var svc = new InventoryService(stream, refData: fixture.RefData, gameConfig: fixture.GameConfig);
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

    [Fact]
    public async Task SubscriberException_DoesNotBreakOtherSubscribers()
    {
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)");
        var svc = new InventoryService(stream);

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
        public IReadOnlyDictionary<long, ItemEntry> Items { get; } = new Dictionary<long, ItemEntry>
        {
            [10251L] = new(10251, "Barley Seeds", "BarleySeeds", 100, 0, []),
        };
        public IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName { get; } = new Dictionary<string, ItemEntry>(StringComparer.Ordinal)
        {
            ["BarleySeeds"] = new(10251, "Barley Seeds", "BarleySeeds", 100, 0, []),
        };
        public ItemKeywordIndex KeywordIndex => new(Items);
        public IReadOnlyDictionary<string, RecipeEntry> Recipes { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, QuestEntry> Quests { get; } = new Dictionary<string, QuestEntry>();
        public IReadOnlyDictionary<string, QuestEntry> QuestsByInternalName { get; } = new Dictionary<string, QuestEntry>();
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

    private sealed class ScriptedStream : IPlayerLogStream
    {
        private readonly Channel<RawLogLine> _channel = Channel.CreateUnbounded<RawLogLine>();
        private long _pending;
        private TaskCompletionSource _drained = NewDrainTcs();

        public ScriptedStream(params string[] lines) : this(lines.Select(l => new RawLogLine(DateTime.UtcNow, l)).ToArray()) { }

        public ScriptedStream(params RawLogLine[] lines)
        {
            if (lines.Length == 0)
            {
                // No initial lines — leave _drained signalled so callers can use Push.
                _drained.TrySetResult();
                return;
            }
            Interlocked.Add(ref _pending, lines.Length);
            foreach (var line in lines) _channel.Writer.TryWrite(line);
        }

        public void Push(string line)
        {
            // Reset drain latch for the next batch so callers can wait on the
            // newly pushed line(s) without a stale completion firing.
            Interlocked.Increment(ref _pending);
            Interlocked.Exchange(ref _drained, NewDrainTcs());
            _channel.Writer.TryWrite(new RawLogLine(DateTime.UtcNow, line));
        }

        public Task WaitForDrainAsync(CancellationToken ct) => _drained.Task.WaitAsync(ct);
        public Task WaitForDrainAsync(TimeSpan timeout) => _drained.Task.WaitAsync(timeout);

        public async IAsyncEnumerable<RawLogLine> SubscribeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var line))
                {
                    yield return line;
                    if (Interlocked.Decrement(ref _pending) == 0)
                        _drained.TrySetResult();
                }
            }
        }

        private static TaskCompletionSource NewDrainTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
