using Arda.Abstractions.Logs;
using Arda.World.Player;
using Arda.World.Player.Events;
using FluentAssertions;
using Legolas.Services;
using Legolas.Tests.TestSupport;

namespace Legolas.Tests.Services;

/// <summary>
/// #497 — a map pin labelled with the character name or the <c>@me</c>
/// sentinel resolves to the declared player position.
/// </summary>
public class CharacterPinAnchorTests
{
    private static LogLineMetadata Meta(DateTimeOffset? at = null)
    {
        var t = at ?? DateTimeOffset.UtcNow;
        return new LogLineMetadata(t, t, false);
    }

    private static (CharacterPinAnchor anchor, TestDomainEventBus bus,
        FakeMapPinState pinState, FakeActiveCharacterService chr, List<int> raised)
        Build(string? name = "Arthas")
    {
        var bus = new TestDomainEventBus();
        var pinState = new FakeMapPinState();
        var chr = new FakeActiveCharacterService();
        if (name is not null) chr.SetName(name);
        var anchor = new CharacterPinAnchor(bus, pinState, chr);
        var raised = new List<int>();
        anchor.Changed += () => raised.Add(1);
        return (anchor, bus, pinState, chr, raised);
    }

    private static void AddPin(TestDomainEventBus bus, FakeMapPinState state,
        double x, double z, string label)
    {
        state.Add(new MapPinEntry(x, z, label, 0, 0));
        bus.Publish(new MapPinAdded(x, z, label, 0, 0, Meta()));
    }

    private static void RemovePin(TestDomainEventBus bus, FakeMapPinState state,
        double x, double z, string label)
    {
        state.Remove(x, z);
        bus.Publish(new MapPinRemoved(x, z, label, Meta()));
    }

    [Fact]
    public void Pin_named_exactly_the_character_is_the_declared_position()
    {
        var (anchor, bus, pinState, _, raised) = Build("Arthas");

        AddPin(bus, pinState, 120, -45, "  arthas ");   // trimmed + case-insensitive

        anchor.Current.Should().NotBeNull();
        anchor.Current!.Value.World.X.Should().Be(120);
        anchor.Current.Value.World.Z.Should().Be(-45);
        raised.Should().ContainSingle();
    }

    [Fact]
    public void At_sign_me_sentinel_matches_even_without_a_known_character()
    {
        var (anchor, bus, pinState, _, _) = Build(name: null);

        AddPin(bus, pinState, 7, 8, "@ME");

        anchor.Current!.Value.World.X.Should().Be(7);
    }

    [Fact]
    public void A_non_matching_pin_is_ignored()
    {
        var (anchor, bus, pinState, _, raised) = Build("Arthas");

        AddPin(bus, pinState, 1, 2, "Iron Vein");

        anchor.Current.Should().BeNull();
        raised.Should().BeEmpty();
    }

    [Fact]
    public void Exact_name_match_outranks_the_me_sentinel_on_snapshot_replay()
    {
        var bus = new TestDomainEventBus();
        var pinState = new FakeMapPinState();
        pinState.Add(new MapPinEntry(50, 50, "@me", 0, 0));
        pinState.Add(new MapPinEntry(99, 99, "Arthas", 0, 0));
        var chr = new FakeActiveCharacterService();
        chr.SetName("Arthas");

        var anchor = new CharacterPinAnchor(bus, pinState, chr);

        anchor.Current!.Value.World.X.Should().Be(99, "the exact-name pin wins the tie");
    }

    [Fact]
    public void Removing_the_active_pin_falls_back_then_clears()
    {
        var (anchor, bus, pinState, _, _) = Build("Arthas");
        AddPin(bus, pinState, 10, 10, "@me");
        AddPin(bus, pinState, 20, 20, "Arthas");   // exact-name now active
        anchor.Current!.Value.World.X.Should().Be(20);

        RemovePin(bus, pinState, 20, 20, "Arthas");   // falls back to the @me pin
        anchor.Current!.Value.World.X.Should().Be(10);

        RemovePin(bus, pinState, 10, 10, "@me");      // nothing left
        anchor.Current.Should().BeNull();
    }

    [Fact]
    public void Area_change_clears_the_declaration()
    {
        var (anchor, bus, pinState, _, _) = Build("Arthas");
        AddPin(bus, pinState, 3, 4, "Arthas");
        anchor.Current.Should().NotBeNull();

        bus.Publish(new AreaChanged(null, "AreaElsewhere", Meta()));

        anchor.Current.Should().BeNull();
    }

    [Fact]
    public void Character_change_re_evaluates_existing_pins()
    {
        var (anchor, bus, pinState, chr, _) = Build("Arthas");
        AddPin(bus, pinState, 11, 22, "Jaina");        // not me yet
        anchor.Current.Should().BeNull();

        chr.SetName("Jaina");             // now it is

        anchor.Current!.Value.World.X.Should().Be(11);
    }

    [Fact]
    public void IsSelfPin_applies_the_same_rule()
    {
        var (anchor, _, _, _, _) = Build("Arthas");
        anchor.IsSelfPin("arthas").Should().BeTrue();
        anchor.IsSelfPin("@me").Should().BeTrue();
        anchor.IsSelfPin("Bank").Should().BeFalse();
    }
}
