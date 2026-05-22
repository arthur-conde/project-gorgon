using FluentAssertions;
using Mithril.GameState.Inventory;
using Mithril.Shared.Logging;
using Mithril.WorldSim;
using Xunit;

namespace Mithril.GameState.Tests.Inventory;

/// <summary>
/// Tests for the world-sim inventory split (#602) — the view layer's
/// typed-frame bus surface + the legacy <c>InventoryEvent</c> shim
/// translation. The view bridges the back-compat
/// <c>Subscribe(Action&lt;InventoryEvent&gt;)</c> consumer surface (six
/// pre-#602 consumers) into the canonical typed-frame surface
/// (<see cref="IInventoryView.Bus"/>) — each kind translates 1:1 into its
/// corresponding view-emitted typed event.
/// </summary>
public sealed class InventoryViewTests
{
    private static DateTime Ts(int s) => new(2026, 5, 22, 8, 0, s, DateTimeKind.Utc);

    /// <summary>
    /// Lightweight backing-service fake. Drives <see cref="InventoryEvent"/>
    /// emissions directly; the view's shim subscription replays + relays each
    /// onto the view's typed bus. Mirrors the real
    /// <c>InventoryService.Subscribe</c> shape: the handler attached during
    /// <see cref="Subscribe"/> sees every later <see cref="Raise"/>.
    /// </summary>
    private sealed class FakeBackingService : IInventoryService
    {
        private readonly List<Action<InventoryEvent>> _handlers = new();
        public bool TryResolve(long instanceId, out string internalName) { internalName = ""; return false; }
        public bool TryGetStackSize(long instanceId, out int stackSize) { stackSize = 0; return false; }
        public IDisposable Subscribe(Action<InventoryEvent> handler, ReplayMode replay = ReplayMode.FromSessionStart)
        {
            _handlers.Add(handler);
            return new Unsub(this, handler);
        }
        public void Raise(InventoryEvent evt)
        {
            foreach (var h in _handlers.ToArray()) h(evt);
        }
        private sealed class Unsub(FakeBackingService o, Action<InventoryEvent> h) : IDisposable
        {
            public void Dispose() => o._handlers.Remove(h);
        }
    }

    [Fact]
    public void Added_event_translates_to_InventoryItemAdded_on_view_bus()
    {
        var fake = new FakeBackingService();
        using var view = new InventoryView(fake);
        var observed = new List<Frame<InventoryItemAdded>>();
        using var _ = view.Bus.Subscribe<InventoryItemAdded>(observed.Add);

        fake.Raise(new InventoryEvent(InventoryEventKind.Added, 42, "Moonstone", Ts(1), 3, SizeConfirmed: true));

        observed.Should().ContainSingle();
        var p = observed[0].Payload;
        p.InstanceId.Should().Be(42L);
        p.InternalName.Should().Be("Moonstone");
        p.StackSize.Should().Be(3);
        p.SizeConfirmed.Should().BeTrue();
        p.Timestamp.Should().Be(Ts(1));
    }

    [Fact]
    public void Deleted_event_translates_to_InventoryItemRemoved_on_view_bus()
    {
        var fake = new FakeBackingService();
        using var view = new InventoryView(fake);
        var observed = new List<Frame<InventoryItemRemoved>>();
        using var _ = view.Bus.Subscribe<InventoryItemRemoved>(observed.Add);

        fake.Raise(new InventoryEvent(InventoryEventKind.Deleted, 42, "Moonstone", Ts(2), 3, SizeConfirmed: true));

        observed.Should().ContainSingle();
        observed[0].Payload.InstanceId.Should().Be(42L);
        observed[0].Payload.InternalName.Should().Be("Moonstone");
    }

    [Fact]
    public void StackChanged_event_translates_to_InventoryStackChanged_on_view_bus()
    {
        var fake = new FakeBackingService();
        using var view = new InventoryView(fake);
        var observed = new List<Frame<InventoryStackChanged>>();
        using var _ = view.Bus.Subscribe<InventoryStackChanged>(observed.Add);

        fake.Raise(new InventoryEvent(InventoryEventKind.StackChanged, 42, "Moonstone", Ts(3), 7, SizeConfirmed: true));

        observed.Should().ContainSingle();
        observed[0].Payload.StackSize.Should().Be(7);
    }

    [Fact]
    public void Shim_Subscribe_delegates_to_backing_service()
    {
        // The legacy union-shaped Subscribe contract must round-trip through
        // the view to the backing service — the six pre-#602 consumers depend
        // on this for back-compat. The view's job is to *also* expose the
        // typed bus surface; not to break the shim.
        var fake = new FakeBackingService();
        using var view = new InventoryView(fake);
        var observed = new List<InventoryEvent>();
        using var _ = view.Subscribe(observed.Add);

        fake.Raise(new InventoryEvent(InventoryEventKind.Added, 42, "Moonstone", Ts(1), 1, SizeConfirmed: false));
        fake.Raise(new InventoryEvent(InventoryEventKind.Deleted, 42, "Moonstone", Ts(2), 1, SizeConfirmed: false));

        observed.Should().HaveCount(2);
        observed[0].Kind.Should().Be(InventoryEventKind.Added);
        observed[1].Kind.Should().Be(InventoryEventKind.Deleted);
    }

    [Fact]
    public void TryResolve_and_TryGetStackSize_delegate_to_backing()
    {
        // The view is a thin facade over the backing service for the query
        // channel — exercising delegation rules out a missed wire-up that
        // would surface as a "data is in the service but not the view" bug.
        var fake = new FakeBackingService();
        using var view = new InventoryView(fake);
        view.TryResolve(123, out _).Should().BeFalse();
        view.TryGetStackSize(123, out _).Should().BeFalse();
    }

    [Fact]
    public void View_disposes_its_backing_subscription()
    {
        // The view's lifetime-owned backing subscription must release on
        // Dispose — otherwise the view leaks past its module lifetime.
        var fake = new FakeBackingService();
        var view = new InventoryView(fake);
        var observed = new List<Frame<InventoryItemAdded>>();
        using var _ = view.Bus.Subscribe<InventoryItemAdded>(observed.Add);

        view.Dispose();
        fake.Raise(new InventoryEvent(InventoryEventKind.Added, 42, "Moonstone", Ts(1), 1, SizeConfirmed: false));

        observed.Should().BeEmpty("after Dispose the view should no longer relay backing events");
    }
}
