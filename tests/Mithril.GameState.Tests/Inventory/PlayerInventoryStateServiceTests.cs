using FluentAssertions;
using Mithril.GameState.Inventory;
using Mithril.GameState.Inventory.Producers;
using Mithril.WorldSim;
using Xunit;

namespace Mithril.GameState.Tests.Inventory;

/// <summary>
/// Tests for the world-sim inventory split (#602) — the PlayerWorld half.
/// Pins folder-level semantics (per-frame change-event emission, retained-on-
/// delete, re-emit suppression) against the new <see cref="IFolder{T}"/>
/// surface. Pre-split equivalents lived in <c>InventoryServiceTests</c> and
/// drove the L1-direct service end-to-end; the new tests target the folder
/// boundary directly because the world-sim's per-frame dispatch is the new
/// integration point.
/// </summary>
public sealed class PlayerInventoryStateServiceTests
{
    private static DateTime Ts(int s) => new(2026, 5, 22, 8, 0, s, DateTimeKind.Utc);
    private static Frame<PlayerInventoryFrame> F(PlayerInventoryFrame p, DateTime ts) =>
        new(new DateTimeOffset(ts, TimeSpan.Zero), p);

    private sealed class StubClock : IWorldClock
    {
        public DateTimeOffset Now => DateTimeOffset.UtcNow;
        public long Frame => 0;
        public WorldMode Mode => WorldMode.Live;
    }

    [Fact]
    public void Add_emits_PlayerInventoryAdded_and_populates_ledger()
    {
        var folder = new PlayerInventoryStateService();
        var changes = folder.Apply(F(new PlayerInventoryAddFrame(42, "Moonstone"), Ts(1)), new StubClock());
        changes.Should().ContainSingle();
        changes[0].Should().BeOfType<PlayerInventoryAdded>().Which.Should().BeEquivalentTo(new
        {
            InstanceId = 42L, InternalName = "Moonstone", Timestamp = Ts(1),
        });

        folder.TryResolve(42, out var n).Should().BeTrue();
        n.Should().Be("Moonstone");
    }

    [Fact]
    public void Remove_emits_PlayerInventoryRemoved_and_retains_resolve()
    {
        var folder = new PlayerInventoryStateService();
        folder.Apply(F(new PlayerInventoryAddFrame(42, "Moonstone"), Ts(1)), new StubClock());
        var changes = folder.Apply(F(new PlayerInventoryRemoveFrame(42), Ts(2)), new StubClock());

        changes.Should().ContainSingle();
        changes[0].Should().BeOfType<PlayerInventoryRemoved>().Which.Should().BeEquivalentTo(new
        {
            InstanceId = 42L, InternalName = "Moonstone", Timestamp = Ts(2),
        });

        // Late-lookup contract: deleted entries still resolve so consumers
        // (Arwen gift-attribution) reading after the remove still see the name.
        folder.TryResolve(42, out var n).Should().BeTrue();
        n.Should().Be("Moonstone");
    }

    [Fact]
    public void Add_reemit_for_alive_instance_does_not_emit_change_event()
    {
        var folder = new PlayerInventoryStateService();
        folder.Apply(F(new PlayerInventoryAddFrame(42, "Moonstone"), Ts(1)), new StubClock());
        var changes = folder.Apply(F(new PlayerInventoryAddFrame(42, "Moonstone"), Ts(2)), new StubClock());
        changes.Should().BeEmpty("re-emission of an already-tracked alive id is a pulse — no second Added");
    }

    [Fact]
    public void Remove_of_unknown_id_is_silent()
    {
        var folder = new PlayerInventoryStateService();
        var changes = folder.Apply(F(new PlayerInventoryRemoveFrame(999), Ts(1)), new StubClock());
        changes.Should().BeEmpty();
        folder.TryResolve(999, out _).Should().BeFalse();
    }

    [Fact]
    public void UpdateItemCode_emits_StackUpdated_with_decoded_size()
    {
        var folder = new PlayerInventoryStateService();
        folder.Apply(F(new PlayerInventoryAddFrame(42, "Moonstone"), Ts(1)), new StubClock());
        // (size-1) << 16 → size 5 => 4 << 16 = 262144
        var code = (long)4 << 16;
        var changes = folder.Apply(F(new PlayerInventoryUpdateItemCodeFrame(42, code), Ts(2)), new StubClock());

        changes.Should().ContainSingle();
        var ev = changes[0].Should().BeOfType<PlayerInventoryStackUpdated>().Subject;
        ev.InstanceId.Should().Be(42L);
        ev.InternalName.Should().Be("Moonstone");
        ev.StackSize.Should().Be(5);
    }

    [Fact]
    public void VaultWithdraw_emits_StackUpdated()
    {
        var folder = new PlayerInventoryStateService();
        folder.Apply(F(new PlayerInventoryAddFrame(42, "Moonstone"), Ts(1)), new StubClock());
        var changes = folder.Apply(F(new PlayerInventoryVaultWithdrawFrame(42, 13), Ts(2)), new StubClock());

        changes.Should().ContainSingle();
        var ev = changes[0].Should().BeOfType<PlayerInventoryStackUpdated>().Subject;
        ev.StackSize.Should().Be(13);
    }

    [Fact]
    public void Stack_update_for_unknown_id_is_silent()
    {
        var folder = new PlayerInventoryStateService();
        folder.Apply(F(new PlayerInventoryUpdateItemCodeFrame(999, 0), Ts(1)), new StubClock()).Should().BeEmpty();
        folder.Apply(F(new PlayerInventoryVaultWithdrawFrame(999, 5), Ts(1)), new StubClock()).Should().BeEmpty();
    }
}

/// <summary>
/// Tests for the world-sim inventory split (#602) — the producer's parser.
/// Validates the regex grammar coverage on representative classified-pipe
/// payloads.
/// </summary>
public sealed class PlayerInventoryFrameProducerParseTests
{
    [Theory]
    [InlineData("ProcessAddItem(Moonstone(42), -1, True)", 42, "Moonstone")]
    [InlineData("ProcessAddItem(AppleJuice(99), -1, False)", 99, "AppleJuice")]
    public void Parse_ProcessAddItem(string data, long expectedId, string expectedName)
    {
        var frame = PlayerInventoryFrameProducer.TryParse(data);
        frame.Should().BeOfType<PlayerInventoryAddFrame>();
        var add = (PlayerInventoryAddFrame)frame!;
        add.InstanceId.Should().Be(expectedId);
        add.InternalName.Should().Be(expectedName);
    }

    [Fact]
    public void Parse_ProcessDeleteItem()
    {
        var frame = PlayerInventoryFrameProducer.TryParse("ProcessDeleteItem(42)");
        frame.Should().BeOfType<PlayerInventoryRemoveFrame>();
        ((PlayerInventoryRemoveFrame)frame!).InstanceId.Should().Be(42);
    }

    [Fact]
    public void Parse_ProcessUpdateItemCode()
    {
        var frame = PlayerInventoryFrameProducer.TryParse("ProcessUpdateItemCode(42, 262144, True)");
        frame.Should().BeOfType<PlayerInventoryUpdateItemCodeFrame>();
        var upd = (PlayerInventoryUpdateItemCodeFrame)frame!;
        upd.InstanceId.Should().Be(42);
        upd.Code.Should().Be(262144);
    }

    [Fact]
    public void Parse_ProcessRemoveFromStorageVault()
    {
        var frame = PlayerInventoryFrameProducer.TryParse("ProcessRemoveFromStorageVault(\"slot\", \"vault\", 42, 13)");
        frame.Should().BeOfType<PlayerInventoryVaultWithdrawFrame>();
        var vw = (PlayerInventoryVaultWithdrawFrame)frame!;
        vw.InstanceId.Should().Be(42);
        vw.StackSize.Should().Be(13);
    }

    [Fact]
    public void Parse_unrelated_line_returns_null()
    {
        PlayerInventoryFrameProducer.TryParse("ProcessSetFavor(NpcKey, 42)").Should().BeNull();
        PlayerInventoryFrameProducer.TryParse("").Should().BeNull();
    }
}
