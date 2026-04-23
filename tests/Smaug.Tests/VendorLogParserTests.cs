using FluentAssertions;
using Smaug.Parsing;
using Xunit;

namespace Smaug.Tests;

public sealed class VendorLogParserTests
{
    private readonly VendorLogParser _parser = new();

    [Fact]
    public void ParsesVendorScreen()
    {
        var line = "[22:27:38] LocalPlayer: ProcessVendorScreen(14564, SoulMates, 3926, 1776704476729, 4000, \"\", VendorInfo[], VendorInfo[], VendorInfo[], VendorPurchaseCap[], [-1201,-1601,], System.String[], -1601)";
        var evt = _parser.TryParse(line, DateTime.UtcNow);

        evt.Should().BeOfType<VendorScreenOpened>();
        var screen = (VendorScreenOpened)evt!;
        screen.EntityId.Should().Be(14564);
        screen.FavorTier.Should().Be("SoulMates");
        screen.RemainingGold.Should().Be(3926);
        screen.GoldCap.Should().Be(4000);
    }

    [Fact]
    public void ParsesVendorAddItem_CapturesExactPrice()
    {
        // Real log line observed in session: the first arg is the sell price.
        var line = "[22:27:48] LocalPlayer: ProcessVendorAddItem(130, RingAugment(78177652), False)";
        var evt = _parser.TryParse(line, DateTime.UtcNow);

        evt.Should().BeOfType<VendorItemSold>();
        var sold = (VendorItemSold)evt!;
        sold.Price.Should().Be(130);
        sold.InternalName.Should().Be("RingAugment");
        sold.InstanceId.Should().Be(78177652);
    }

    [Fact]
    public void ParsesVendorGoldUpdate()
    {
        var line = "[22:27:48] LocalPlayer: ProcessVendorUpdateAvailableGold(3796, 1776704476729, 4000)";
        var evt = _parser.TryParse(line, DateTime.UtcNow);

        evt.Should().BeOfType<VendorGoldUpdated>();
        var g = (VendorGoldUpdated)evt!;
        g.RemainingGold.Should().Be(3796);
        g.GoldCap.Should().Be(4000);
    }

    [Fact]
    public void ParsesStartInteraction()
    {
        var line = "[22:27:38] LocalPlayer: ProcessStartInteraction(14564, 0, 3926, True, \"NPC_Marna\")";
        var evt = _parser.TryParse(line, DateTime.UtcNow);

        evt.Should().BeOfType<NpcInteractionStarted>();
        var s = (NpcInteractionStarted)evt!;
        s.EntityId.Should().Be(14564);
        s.NpcKey.Should().Be("NPC_Marna");
    }

    [Fact]
    public void ParsesCivicPrideFromLoadSkills()
    {
        // Embedded within the giant ProcessLoadSkills line; only the substring matters.
        var line = "ProcessLoadSkills(... {type=CivicPride,raw=1,bonus=1,xp=45,tnl=60,max=50}, ...)";
        var evt = _parser.TryParse(line, DateTime.UtcNow);

        evt.Should().BeOfType<CivicPrideUpdated>();
        var cp = (CivicPrideUpdated)evt!;
        cp.Raw.Should().Be(1);
        cp.Bonus.Should().Be(1);
        cp.EffectiveLevel.Should().Be(2);
    }

    [Fact]
    public void ReturnsNullForUnrelatedLine()
    {
        _parser.TryParse("[00:00:00] NullAnimEx.SetLocomotionMode()", DateTime.UtcNow).Should().BeNull();
        _parser.TryParse("", DateTime.UtcNow).Should().BeNull();
    }
}
