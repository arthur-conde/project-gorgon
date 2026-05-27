using Arda.Abstractions.Logs;
using Arda.Composition.Events;
using Arda.Composition.Internal;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.Reference.Models.Npcs;
using Mithril.Shared.Character;
using Xunit;

namespace Arda.Composition.Tests;

public class NpcStateComposerTests : IDisposable
{
    private readonly DomainEventBus _bus = new(NullLogger<DomainEventBus>.Instance);
    private readonly NpcStateComposer _composer;
    private readonly List<NpcStateChanged> _stateChangedEvents = [];

    private static readonly DateTimeOffset T0 = new(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset GoldResetTime = new(2026, 5, 27, 11, 0, 0, TimeSpan.Zero);

    public NpcStateComposerTests()
    {
        _composer = new NpcStateComposer(_bus);
        _bus.Subscribe<NpcStateChanged>(e => _stateChangedEvents.Add(e));
    }

    public void Dispose() => _composer.Dispose();

    private static LogLineMetadata Meta(DateTimeOffset ts) =>
        new(Timestamp: ts, ReadOn: ts, IsReplay: false);

    // ── InteractionStarted ─────────────────────────────────────────────────

    [Fact]
    public void InteractionStarted_PopulatesNpcWithFavor()
    {
        _bus.Publish(new InteractionStarted(12307, "NPC_Marna", 2847.3, true, Meta(T0)));

        _composer.Npcs.Should().ContainKey("NPC_Marna");
        var record = _composer.Npcs["NPC_Marna"];
        record.AbsoluteFavor.Should().Be(2847.3);
        record.FavorUpdatedAt.Should().Be(T0);
        record.LastSeenAt.Should().Be(T0);
    }

    [Fact]
    public void InteractionStarted_NonNpc_Ignored()
    {
        _bus.Publish(new InteractionStarted(5000, "SomeChest", 0, false, Meta(T0)));

        _composer.Npcs.Should().BeEmpty();
    }

    [Fact]
    public void InteractionStarted_EmptyName_Ignored()
    {
        _bus.Publish(new InteractionStarted(5000, "", 0, true, Meta(T0)));

        _composer.Npcs.Should().BeEmpty();
    }

    [Fact]
    public void InteractionStarted_OverwritesFavor()
    {
        _bus.Publish(new InteractionStarted(12307, "NPC_Marna", 2847.3, true, Meta(T0)));
        _bus.Publish(new InteractionStarted(12307, "NPC_Marna", 2900.0, true, Meta(T0.AddMinutes(5))));

        _composer.Npcs["NPC_Marna"].AbsoluteFavor.Should().Be(2900.0);
        _composer.Npcs["NPC_Marna"].FavorUpdatedAt.Should().Be(T0.AddMinutes(5));
    }

    [Fact]
    public void InteractionStarted_FiresStateChanged()
    {
        var fired = false;
        _composer.StateChanged += () => fired = true;

        _bus.Publish(new InteractionStarted(12307, "NPC_Marna", 100, true, Meta(T0)));

        fired.Should().BeTrue();
    }

    [Fact]
    public void InteractionStarted_PublishesNpcStateChangedEvent()
    {
        _bus.Publish(new InteractionStarted(12307, "NPC_Marna", 2847.3, true, Meta(T0)));

        _stateChangedEvents.Should().ContainSingle();
        _stateChangedEvents[0].NpcKey.Should().Be("NPC_Marna");
        _stateChangedEvents[0].Record.AbsoluteFavor.Should().Be(2847.3);
    }

    // ── VendorScreenOpened ─────────────────────────────────────────────────

    [Fact]
    public void VendorScreenOpened_PopulatesTierAndGold()
    {
        _bus.Publish(new VendorScreenOpened(
            12307, "Comfortable", 48637, 50000, GoldResetTime, "NPC_Marna", Meta(T0)));

        _composer.Npcs.Should().ContainKey("NPC_Marna");
        var record = _composer.Npcs["NPC_Marna"];
        record.FavorTier.Should().Be(FavorTier.Comfortable);
        record.RemainingGold.Should().Be(48637);
        record.GoldCap.Should().Be(50000);
        record.GoldResetsAt.Should().Be(GoldResetTime);
        record.GoldUpdatedAt.Should().Be(T0);
    }

    [Fact]
    public void VendorScreenOpened_NullNpcKey_Ignored()
    {
        _bus.Publish(new VendorScreenOpened(
            12307, "Comfortable", 48637, 50000, GoldResetTime, null, Meta(T0)));

        _composer.Npcs.Should().BeEmpty();
    }

    [Fact]
    public void VendorScreenOpened_PreservesFavorFromPriorInteraction()
    {
        _bus.Publish(new InteractionStarted(12307, "NPC_Marna", 2847.3, true, Meta(T0)));
        _bus.Publish(new VendorScreenOpened(
            12307, "Comfortable", 48637, 50000, GoldResetTime, "NPC_Marna", Meta(T0.AddSeconds(1))));

        var record = _composer.Npcs["NPC_Marna"];
        record.AbsoluteFavor.Should().Be(2847.3);
        record.FavorTier.Should().Be(FavorTier.Comfortable);
        record.RemainingGold.Should().Be(48637);
    }

    // ── VendorGoldUpdated ──────────────────────────────────────────────────

    [Fact]
    public void VendorGoldUpdated_UpdatesCurrentVendorNpc()
    {
        _bus.Publish(new VendorScreenOpened(
            12307, "Comfortable", 48637, 50000, GoldResetTime, "NPC_Marna", Meta(T0)));
        _bus.Publish(new VendorGoldUpdated(47000, 50000, GoldResetTime, Meta(T0.AddSeconds(5))));

        _composer.Npcs["NPC_Marna"].RemainingGold.Should().Be(47000);
        _composer.Npcs["NPC_Marna"].GoldUpdatedAt.Should().Be(T0.AddSeconds(5));
    }

    [Fact]
    public void VendorGoldUpdated_NoVendorSession_IsNoOp()
    {
        _bus.Publish(new VendorGoldUpdated(47000, 50000, GoldResetTime, Meta(T0)));

        _composer.Npcs.Should().BeEmpty();
    }

    // ── GiftAccepted ───────────────────────────────────────────────────────

    [Fact]
    public void GiftAccepted_AppliesDeltaToExistingFavor()
    {
        _bus.Publish(new InteractionStarted(12307, "NPC_Tadion", 455.1, true, Meta(T0)));
        _bus.Publish(new GiftAccepted(12307, "NPC_Tadion", 99999, 12.5, Meta(T0.AddSeconds(2))));

        _composer.Npcs["NPC_Tadion"].AbsoluteFavor.Should().BeApproximately(467.6, 0.01);
        _composer.Npcs["NPC_Tadion"].FavorUpdatedAt.Should().Be(T0.AddSeconds(2));
    }

    [Fact]
    public void GiftAccepted_NoBaseline_UsesDeltaAsAbsolute()
    {
        _bus.Publish(new GiftAccepted(12307, "NPC_Unknown", 99999, 12.5, Meta(T0)));

        _composer.Npcs["NPC_Unknown"].AbsoluteFavor.Should().Be(12.5);
    }

    [Fact]
    public void InteractionStarted_AfterGift_OverwritesFavor_SelfHealing()
    {
        _bus.Publish(new InteractionStarted(12307, "NPC_Tadion", 455.1, true, Meta(T0)));
        _bus.Publish(new GiftAccepted(12307, "NPC_Tadion", 99999, 12.5, Meta(T0.AddSeconds(2))));
        _bus.Publish(new InteractionStarted(12307, "NPC_Tadion", 470.0, true, Meta(T0.AddSeconds(5))));

        _composer.Npcs["NPC_Tadion"].AbsoluteFavor.Should().Be(470.0,
            "absolute reading from interaction overwrites computed delta");
    }

    // ── GetNpc ─────────────────────────────────────────────────────────────

    [Fact]
    public void GetNpc_ReturnsRecordIfExists()
    {
        _bus.Publish(new InteractionStarted(12307, "NPC_Marna", 100, true, Meta(T0)));

        _composer.GetNpc("NPC_Marna").Should().NotBeNull();
        _composer.GetNpc("NPC_Marna")!.Value.AbsoluteFavor.Should().Be(100);
    }

    [Fact]
    public void GetNpc_ReturnsNullIfNotFound()
    {
        _composer.GetNpc("NPC_Nobody").Should().BeNull();
    }

    // ── Session switch (no store) ──────────────────────────────────────────

    [Fact]
    public void SessionEstablished_ClearsStateWithoutStore()
    {
        _bus.Publish(new InteractionStarted(12307, "NPC_Marna", 100, true, Meta(T0)));

        var session = new ComposedSession("Alice", "TestServer",
            T0.AddMinutes(5), TimeSpan.Zero, "Alice:20260526120500");
        _bus.Publish(new SessionEstablished(session, Meta(T0.AddMinutes(5))));

        _composer.Npcs.Should().BeEmpty("no store means state is cleared on session switch");
    }

    [Fact]
    public void SameSession_DoesNotReload()
    {
        var session = new ComposedSession("Alice", "TestServer",
            T0, TimeSpan.Zero, "Alice:20260526120000");
        _bus.Publish(new SessionEstablished(session, Meta(T0)));

        _bus.Publish(new InteractionStarted(12307, "NPC_Marna", 100, true, Meta(T0.AddSeconds(1))));

        var changeCount = 0;
        _composer.StateChanged += () => changeCount++;

        _bus.Publish(new SessionEstablished(session, Meta(T0)));

        changeCount.Should().Be(0, "same session does not trigger reload");
    }

    [Fact]
    public void DifferentSession_FiresStateChanged()
    {
        var session1 = new ComposedSession("Alice", "TestServer",
            T0, TimeSpan.Zero, "Alice:20260526120000");
        _bus.Publish(new SessionEstablished(session1, Meta(T0)));

        var fired = false;
        _composer.StateChanged += () => fired = true;

        var session2 = new ComposedSession("Bob", "TestServer",
            T0.AddMinutes(10), TimeSpan.Zero, "Bob:20260526121000");
        _bus.Publish(new SessionEstablished(session2, Meta(T0.AddMinutes(10))));

        fired.Should().BeTrue();
    }

    // ── Persistence round-trip ─────────────────────────────────────────────

    [Fact]
    public void PersistenceRoundTrip_RestoresState()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"mithril-test-{Guid.NewGuid():N}");
        try
        {
            var store = new PerCharacterStore<NpcStateSnapshot>(
                tempDir,
                "npc-state.json",
                NpcStateSnapshotJsonContext.Default.NpcStateSnapshot);

            using (var composer1 = new NpcStateComposer(_bus, store))
            {
                var session = new ComposedSession("Alice", "Server1",
                    T0, TimeSpan.Zero, "Alice:20260526120000");
                _bus.Publish(new SessionEstablished(session, Meta(T0)));

                _bus.Publish(new InteractionStarted(12307, "NPC_Marna", 2847.3, true, Meta(T0.AddSeconds(1))));
                _bus.Publish(new VendorScreenOpened(
                    12307, "Comfortable", 48637, 50000, GoldResetTime, "NPC_Marna", Meta(T0.AddSeconds(2))));
            }

            using var composer2 = new NpcStateComposer(_bus, store);

            var session2 = new ComposedSession("Alice", "Server1",
                T0.AddHours(1), TimeSpan.Zero, "Alice:20260526130000");
            _bus.Publish(new SessionEstablished(session2, Meta(T0.AddHours(1))));

            composer2.Npcs.Should().ContainKey("NPC_Marna");
            composer2.Npcs["NPC_Marna"].AbsoluteFavor.Should().Be(2847.3);
            composer2.Npcs["NPC_Marna"].FavorTier.Should().Be(FavorTier.Comfortable);
            composer2.Npcs["NPC_Marna"].RemainingGold.Should().Be(48637);
            composer2.Npcs["NPC_Marna"].GoldCap.Should().Be(50000);
            composer2.Npcs["NPC_Marna"].GoldResetsAt.Should().Be(GoldResetTime);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CharacterSwitch_FlushesOldAndLoadsNew()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"mithril-test-{Guid.NewGuid():N}");
        try
        {
            var store = new PerCharacterStore<NpcStateSnapshot>(
                tempDir,
                "npc-state.json",
                NpcStateSnapshotJsonContext.Default.NpcStateSnapshot);

            using var composer = new NpcStateComposer(_bus, store);

            var aliceSession = new ComposedSession("Alice", "Server1",
                T0, TimeSpan.Zero, "Alice:20260526120000");
            _bus.Publish(new SessionEstablished(aliceSession, Meta(T0)));
            _bus.Publish(new InteractionStarted(12307, "NPC_Marna", 100, true, Meta(T0.AddSeconds(1))));

            var bobSession = new ComposedSession("Bob", "Server1",
                T0.AddMinutes(10), TimeSpan.Zero, "Bob:20260526121000");
            _bus.Publish(new SessionEstablished(bobSession, Meta(T0.AddMinutes(10))));

            composer.Npcs.Should().BeEmpty("Bob has no NPC data yet");

            _bus.Publish(new InteractionStarted(5000, "NPC_Johen", 50, true, Meta(T0.AddMinutes(11))));
            composer.Npcs.Should().ContainKey("NPC_Johen");
            composer.Npcs.Should().NotContainKey("NPC_Marna");

            _bus.Publish(new SessionEstablished(aliceSession, Meta(T0)));
            composer.Npcs.Should().ContainKey("NPC_Marna");
            composer.Npcs["NPC_Marna"].AbsoluteFavor.Should().Be(100);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── Merge semantics ────────────────────────────────────────────────────

    [Fact]
    public void PersistedNpcs_NotInReplay_Survive()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"mithril-test-{Guid.NewGuid():N}");
        try
        {
            var store = new PerCharacterStore<NpcStateSnapshot>(
                tempDir,
                "npc-state.json",
                NpcStateSnapshotJsonContext.Default.NpcStateSnapshot);

            using (var composer1 = new NpcStateComposer(_bus, store))
            {
                var session = new ComposedSession("Alice", "Server1",
                    T0, TimeSpan.Zero, "Alice:20260526120000");
                _bus.Publish(new SessionEstablished(session, Meta(T0)));
                _bus.Publish(new InteractionStarted(12307, "NPC_Marna", 100, true, Meta(T0.AddSeconds(1))));
                _bus.Publish(new InteractionStarted(5000, "NPC_Tadion", 200, true, Meta(T0.AddSeconds(2))));
            }

            using var composer2 = new NpcStateComposer(_bus, store);
            var session2 = new ComposedSession("Alice", "Server1",
                T0.AddHours(1), TimeSpan.Zero, "Alice:20260526130000");
            _bus.Publish(new SessionEstablished(session2, Meta(T0.AddHours(1))));

            composer2.Npcs.Should().HaveCount(2);
            composer2.Npcs.Should().ContainKey("NPC_Marna");
            composer2.Npcs.Should().ContainKey("NPC_Tadion");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── Dispose ────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_StopsSubscriptions()
    {
        _composer.Dispose();

        _bus.Publish(new InteractionStarted(12307, "NPC_Marna", 100, true, Meta(T0)));

        _stateChangedEvents.Should().BeEmpty();
        _composer.Npcs.Should().BeEmpty();
    }
}
