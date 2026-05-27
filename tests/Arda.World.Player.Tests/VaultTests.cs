using System.Collections.Frozen;
using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

public class VaultTests
{
    private readonly SpyEventBus _bus = new();
    private readonly Inventory _inventory;
    private readonly Vault _vault;

    public VaultTests()
    {
        var pool = new InternPool(FrozenDictionary<string, string>.Empty);
        _inventory = new Inventory(_bus, pool);
        _vault = new Vault(_bus, _inventory);
    }

    private static LogLineMetadata Meta() =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false);

    // ── Withdrawal: stack-size correction ────────────────────────────────

    [Fact]
    public void Withdrawal_CorrectsStackSize()
    {
        // ProcessAddItem sets StackSize = 1
        _inventory.OnAddItem("(Amethyst(54162830), 132, True)".AsSpan(), "", Meta());
        _inventory.Items[54162830].StackSize.Should().Be(1);

        // Vault stashes the pending add
        _vault.OnAddItem("(Amethyst(54162830), 132, True)".AsSpan(), "", Meta());

        _bus.Clear();

        // ProcessRemoveFromStorageVault corrects to 93
        _vault.OnRemoveFromStorageVault("(30346037, -1, 54162830, 93)".AsSpan(), default, "", Meta());

        _inventory.Items[54162830].StackSize.Should().Be(93);
    }

    [Fact]
    public void Withdrawal_EmitsInventoryItemUpdated()
    {
        _inventory.OnAddItem("(Diamond(87037617), -1, False)".AsSpan(), "", Meta());
        _vault.OnAddItem("(Diamond(87037617), -1, False)".AsSpan(), "", Meta());
        _bus.Clear();

        _vault.OnRemoveFromStorageVault("(30346037, -1, 87037617, 5)".AsSpan(), default, "", Meta());

        _bus.Published<InventoryItemUpdated>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                InstanceId = 87037617L,
                NewStackSize = 5,
                PreviousStackSize = 1
            });
    }

    [Fact]
    public void Withdrawal_EmitsVaultWithdrawalEvent()
    {
        _inventory.OnAddItem("(Amethyst(54162830), 132, True)".AsSpan(), "", Meta());
        _vault.OnAddItem("(Amethyst(54162830), 132, True)".AsSpan(), "", Meta());
        _bus.Clear();

        _vault.OnRemoveFromStorageVault("(30346037, -1, 54162830, 93)".AsSpan(), default, "", Meta());

        _bus.Published<VaultWithdrawal>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                InstanceId = 54162830L,
                InternalName = "Amethyst",
                StackCount = 93,
                VaultEntityId = 30346037L
            });
    }

    [Fact]
    public void Withdrawal_WithStackSizeOne_DoesNotEmitUpdated()
    {
        _inventory.OnAddItem("(Sword(111), -1, False)".AsSpan(), "", Meta());
        _vault.OnAddItem("(Sword(111), -1, False)".AsSpan(), "", Meta());
        _bus.Clear();

        // Stack count = 1, same as what ProcessAddItem already set
        _vault.OnRemoveFromStorageVault("(999, -1, 111, 1)".AsSpan(), default, "", Meta());

        _bus.Published<InventoryItemUpdated>().Should().BeEmpty();
        _bus.Published<VaultWithdrawal>().Should().ContainSingle();
    }

    [Fact]
    public void Withdrawal_WithoutPrecedingAddItem_StillCorrects()
    {
        // ProcessAddItem arrived (Inventory knows it) but Vault didn't see OnAddItem
        _inventory.OnAddItem("(Gem(222), -1, False)".AsSpan(), "", Meta());
        _bus.Clear();

        _vault.OnRemoveFromStorageVault("(999, -1, 222, 10)".AsSpan(), default, "", Meta());

        // Stack size still corrected via Inventory.CorrectStackSize
        _inventory.Items[222].StackSize.Should().Be(10);
        // InternalName resolved from inventory state
        _bus.Published<VaultWithdrawal>().Should().ContainSingle()
            .Which.InternalName.Should().Be("Gem");
    }

    [Fact]
    public void Withdrawal_UnknownInstanceId_NoStackCorrection()
    {
        // No ProcessAddItem — instance not in inventory
        _vault.OnRemoveFromStorageVault("(999, -1, 999999, 50)".AsSpan(), default, "", Meta());

        _bus.Published<InventoryItemUpdated>().Should().BeEmpty();
        _bus.Published<VaultWithdrawal>().Should().ContainSingle();
    }

    // ── Deposit ──────────────────────────────────────────────────────────

    [Fact]
    public void Deposit_EmitsVaultDepositEvent()
    {
        _inventory.OnAddItem("(Moonstone(333), -1, False)".AsSpan(), "", Meta());
        // ProcessDeleteItem removes it from inventory
        _inventory.OnDeleteItem("(333)".AsSpan(), "", Meta());
        // Vault stashes the pending delete
        _vault.OnDeleteItem("(333)".AsSpan(), "", Meta());
        _bus.Clear();

        // ProcessAddToStorageVault correlates with the pending delete
        _vault.OnAddToStorageVault("(30346037, -1, 5, Moonstone(333))".AsSpan(), default, "", Meta());

        _bus.Published<VaultDeposit>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                InstanceId = 333L,
                InternalName = "Moonstone",
                VaultEntityId = 30346037L,
                SlotIndex = 5
            });
    }

    [Fact]
    public void Deposit_WithoutPrecedingDeleteItem_StillEmits()
    {
        // No ProcessDeleteItem seen by Vault — but ProcessAddToStorageVault still fires
        _vault.OnAddToStorageVault("(30346037, -1, 2, Ruby(444))".AsSpan(), default, "", Meta());

        _bus.Published<VaultDeposit>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                InstanceId = 444L,
                InternalName = "Ruby",
                VaultEntityId = 30346037L,
                SlotIndex = 2
            });
    }

    // ── ShowStorageVault ──────────────────────────────────────────────────

    [Fact]
    public void ShowStorageVault_EmitsVaultOpenedEvent()
    {
        _vault.OnShowStorageVault("(30346037, 100, \"My Vault\", \"A storage vault.\", 30, System.Collections.Generic.List`1[Item], , , , , 0)".AsSpan(), default, "", Meta());

        _bus.Published<VaultOpened>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                EntityId = 30346037L,
                StorageId = 100L,
                Label = "My Vault",
                SlotCount = 30
            });
    }

    [Fact]
    public void ShowStorageVault_SetsVaultEntityForSubsequentEvents()
    {
        _vault.OnShowStorageVault("(12345, 200, \"Saddlebag\", \"Your saddlebag.\", 16, System.Collections.Generic.List`1[Item], , , , , 0)".AsSpan(), default, "", Meta());

        _inventory.OnAddItem("(Gold(555), -1, False)".AsSpan(), "", Meta());
        _vault.OnAddItem("(Gold(555), -1, False)".AsSpan(), "", Meta());
        _bus.Clear();

        _vault.OnRemoveFromStorageVault("(99999, -1, 555, 100)".AsSpan(), default, "", Meta());

        // VaultEntityId comes from the ProcessShowStorageVault session, not the verb arg
        _bus.Published<VaultWithdrawal>().Should().ContainSingle()
            .Which.VaultEntityId.Should().Be(12345L);
    }

    // ── Reset ────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsPendingState()
    {
        _inventory.OnAddItem("(Ore(666), -1, False)".AsSpan(), "", Meta());
        _vault.OnAddItem("(Ore(666), -1, False)".AsSpan(), "", Meta());

        _vault.Reset();
        _bus.Clear();

        // After reset, the pending add should be gone — withdrawal still corrects
        // via direct inventory lookup, but pending name resolution is cleared
        _vault.OnRemoveFromStorageVault("(999, -1, 666, 20)".AsSpan(), default, "", Meta());

        _inventory.Items[666].StackSize.Should().Be(20);
        // Name resolved from inventory state (not pending)
        _bus.Published<VaultWithdrawal>().Should().ContainSingle()
            .Which.InternalName.Should().Be("Ore");
    }

    [Fact]
    public void Reset_ClearsVaultSession()
    {
        _vault.OnShowStorageVault("(12345, 200, \"Vault\", \"desc\", 16, System.Collections.Generic.List`1[Item], , , , , 0)".AsSpan(), default, "", Meta());

        _vault.Reset();

        _inventory.OnAddItem("(Item(777), -1, False)".AsSpan(), "", Meta());
        _vault.OnAddItem("(Item(777), -1, False)".AsSpan(), "", Meta());
        _bus.Clear();

        _vault.OnRemoveFromStorageVault("(99999, -1, 777, 3)".AsSpan(), default, "", Meta());

        // After reset, vault entity falls back to the arg from ProcessRemoveFromStorageVault
        _bus.Published<VaultWithdrawal>().Should().ContainSingle()
            .Which.VaultEntityId.Should().Be(99999L);
    }

    // ── SpyEventBus ──────────────────────────────────────────────────────

    private sealed class SpyEventBus : IDomainEventBus
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
