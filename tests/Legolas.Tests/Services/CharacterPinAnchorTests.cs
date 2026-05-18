using FluentAssertions;
using Legolas.Services;

namespace Legolas.Tests.Services;

/// <summary>
/// #497 — a map pin labelled with the character name or the <c>@me</c>
/// sentinel resolves to the declared player position.
/// </summary>
public class CharacterPinAnchorTests
{
    private static (CharacterPinAnchor anchor, FakePlayerPinTracker pins,
        FakeActiveCharacterService chr, List<int> raised) Build(string? name = "Arthas")
    {
        var pins = new FakePlayerPinTracker();
        var chr = new FakeActiveCharacterService();
        if (name is not null) chr.SetName(name);
        var anchor = new CharacterPinAnchor(pins, chr);
        var raised = new List<int>();
        anchor.Changed += () => raised.Add(1);
        return (anchor, pins, chr, raised);
    }

    [Fact]
    public void Pin_named_exactly_the_character_is_the_declared_position()
    {
        var (anchor, pins, _, raised) = Build("Arthas");

        pins.Add(120, -45, "  arthas ");   // trimmed + case-insensitive

        anchor.Current.Should().NotBeNull();
        anchor.Current!.Value.World.X.Should().Be(120);
        anchor.Current.Value.World.Z.Should().Be(-45);
        raised.Should().ContainSingle();
    }

    [Fact]
    public void At_sign_me_sentinel_matches_even_without_a_known_character()
    {
        var (anchor, pins, _, _) = Build(name: null);

        pins.Add(7, 8, "@ME");

        anchor.Current!.Value.World.X.Should().Be(7);
    }

    [Fact]
    public void A_non_matching_pin_is_ignored()
    {
        var (anchor, pins, _, raised) = Build("Arthas");

        pins.Add(1, 2, "Iron Vein");

        anchor.Current.Should().BeNull();
        raised.Should().BeEmpty();
    }

    [Fact]
    public void Exact_name_match_outranks_the_me_sentinel_on_snapshot_replay()
    {
        var pins = new FakePlayerPinTracker();
        pins.SeedExisting(
            FakePlayerPinTracker.Pin(50, 50, "@me"),
            FakePlayerPinTracker.Pin(99, 99, "Arthas"));
        var chr = new FakeActiveCharacterService();
        chr.SetName("Arthas");

        // Subscribe replays the seeded set as a Snapshot.
        var anchor = new CharacterPinAnchor(pins, chr);

        anchor.Current!.Value.World.X.Should().Be(99, "the exact-name pin wins the tie");
    }

    [Fact]
    public void Removing_the_active_pin_falls_back_then_clears()
    {
        var (anchor, pins, _, _) = Build("Arthas");
        var other = pins.Add(10, 10, "@me");
        var named = pins.Add(20, 20, "Arthas");   // exact-name now active
        anchor.Current!.Value.World.X.Should().Be(20);

        pins.Remove(named);                        // falls back to the @me pin
        anchor.Current!.Value.World.X.Should().Be(10);

        pins.Remove(other);                        // nothing left
        anchor.Current.Should().BeNull();
    }

    [Fact]
    public void Area_change_clears_the_declaration()
    {
        var (anchor, pins, _, _) = Build("Arthas");
        pins.Add(3, 4, "Arthas");
        anchor.Current.Should().NotBeNull();

        pins.ChangeArea("AreaElsewhere");

        anchor.Current.Should().BeNull();
    }

    [Fact]
    public void Character_change_re_evaluates_existing_pins()
    {
        var (anchor, pins, chr, _) = Build("Arthas");
        pins.Add(11, 22, "Jaina");        // not me yet
        anchor.Current.Should().BeNull();

        chr.SetName("Jaina");             // now it is

        anchor.Current!.Value.World.X.Should().Be(11);
    }

    [Fact]
    public void IsSelfPin_applies_the_same_rule()
    {
        var (anchor, _, _, _) = Build("Arthas");
        anchor.IsSelfPin(FakePlayerPinTracker.Pin(0, 0, "arthas")).Should().BeTrue();
        anchor.IsSelfPin(FakePlayerPinTracker.Pin(0, 0, "@me")).Should().BeTrue();
        anchor.IsSelfPin(FakePlayerPinTracker.Pin(0, 0, "Bank")).Should().BeFalse();
    }
}
