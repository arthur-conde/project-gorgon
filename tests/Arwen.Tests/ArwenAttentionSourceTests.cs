using System.IO;
using Arwen.Domain;
using FluentAssertions;
using Mithril.Shared.Inventory;
using Mithril.Shared.Reference;
using Xunit;

namespace Arwen.Tests;

[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class ArwenAttentionSourceTests
{
    private static (CalibrationService svc, FakeInventory inv) BuildService(string dataDir)
    {
        var items = new Dictionary<long, ItemEntry>
        {
            [7] = new(7, "Phlogiston1", "Phlogiston1", MaxStackSize: 10, IconId: 0,
                [new ItemKeyword("Crystal", 0), new ItemKeyword("Moonstone", 500)],
                Value: 5m),
        };
        var npcs = new Dictionary<string, NpcEntry>(StringComparer.Ordinal)
        {
            ["NPC_Sanja"] = new("NPC_Sanja", "Sanja", "Serbule",
                [new NpcPreference("Love", ["Moonstone"], "Moonstones", 1.5, null)],
                ["Friends"], []),
        };
        var refData = new FakeRefData(items, npcs);
        var index = new GiftIndex();
        index.Build(refData.Items, refData.Npcs);
        var inv = new FakeInventory();
        var svc = new CalibrationService(refData, index, inv, dataDir);
        return (svc, inv);
    }

    private static void SafeDeleteDir(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void IdentityAndLabel_AreStable()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_attn");
        try
        {
            var (svc, _) = BuildService(dir);
            var src = new ArwenAttentionSource(svc);

            src.ModuleId.Should().Be("arwen");
            src.DisplayLabel.Should().NotBeNullOrWhiteSpace();
        }
        finally { SafeDeleteDir(dir); }
    }

    [Fact]
    public void Count_IsZero_WhenNoPending()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_attn");
        try
        {
            var (svc, _) = BuildService(dir);
            var src = new ArwenAttentionSource(svc);

            src.Count.Should().Be(0);
        }
        finally { SafeDeleteDir(dir); }
    }

    [Fact]
    public void Count_TracksPending_FiresChangedOnEnqueueAndConfirm()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_attn");
        try
        {
            var (svc, inv) = BuildService(dir);
            var src = new ArwenAttentionSource(svc);
            var fires = 0;
            src.Changed += (_, _) => fires++;

            // Enqueue a stackable gift with unknown stack size — lands in pending.
            inv.Add(9999, "Phlogiston1", stackSize: 0);
            svc.OnStartInteraction("NPC_Sanja");
            svc.OnItemDeleted(9999);
            svc.OnDeltaFavor("NPC_Sanja", 13.2);

            src.Count.Should().Be(1);
            fires.Should().BeGreaterThanOrEqualTo(1);
            var firesAfterEnqueue = fires;

            var pending = svc.PendingObservations.Single();
            svc.ConfirmPending(pending.Id, 5).Should().BeTrue();

            src.Count.Should().Be(0);
            fires.Should().BeGreaterThan(firesAfterEnqueue);
        }
        finally { SafeDeleteDir(dir); }
    }

    [Fact]
    public void Count_TracksPending_FiresChangedOnDiscard()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_attn");
        try
        {
            var (svc, inv) = BuildService(dir);
            var src = new ArwenAttentionSource(svc);

            inv.Add(9999, "Phlogiston1", stackSize: 0);
            svc.OnStartInteraction("NPC_Sanja");
            svc.OnItemDeleted(9999);
            svc.OnDeltaFavor("NPC_Sanja", 13.2);

            var fires = 0;
            src.Changed += (_, _) => fires++;

            var pending = svc.PendingObservations.Single();
            svc.DiscardPending(pending.Id).Should().BeTrue();

            src.Count.Should().Be(0);
            fires.Should().BeGreaterThanOrEqualTo(1);
        }
        finally { SafeDeleteDir(dir); }
    }
}
