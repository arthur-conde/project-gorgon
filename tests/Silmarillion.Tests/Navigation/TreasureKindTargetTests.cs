using System.Linq;
using FluentAssertions;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.Navigation;

public sealed class TreasureKindTargetTests
{
    private static (PowerKindTarget Power, ProfileKindTarget Profile, TreasureTabViewModel Vm) Build()
    {
        var d = new TreasureFakeReferenceData();
        d.AddSkill("Sword", "Sword");
        d.AddPower(new PowerEntry("SwordBoost", "Sword", new[] { "Head" }, "of Swordsmanship",
            new Dictionary<int, PowerTier> { [1] = new PowerTier(1, new[] { "{BOOST_SKILL_SWORD}{5}" }, 20, MinLevel: 1, MinRarity: "Uncommon", SkillLevelPrereq: 1) },
            Prefix: "Swordsman's", EnvelopeKey: "power_1001"));
        d.AddProfile("Sword", "SwordBoost");
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var vm = new TreasureTabViewModel(d, nav, new ReferenceDataEntityNameResolver(d));
        return (new PowerKindTarget(vm), new ProfileKindTarget(vm), vm);
    }

    [Fact]
    public void Kinds_ArePowerAndProfile_BothTabIndexTen()
    {
        var (power, profile, _) = Build();
        power.Kind.Should().Be(EntityKind.Power);
        profile.Kind.Should().Be(EntityKind.Profile);
        power.TabIndex.Should().Be(10);
        profile.TabIndex.Should().Be(10);
    }

    [Fact]
    public void PowerKindTarget_SelectsPowerRow_BuildsPowerDetail()
    {
        var (power, _, vm) = Build();

        power.TrySelectByInternalName("SwordBoost").Should().BeTrue();

        vm.SelectedRow.Should().NotBeNull();
        vm.SelectedRow!.Kind.Should().Be(TreasureRowKind.Power);
        vm.SelectedRow.InternalName.Should().Be("SwordBoost");
        vm.DetailViewModel.Should().BeOfType<PowerDetailViewModel>();
    }

    [Fact]
    public void ProfileKindTarget_SelectsProfileRow_BuildsProfileDetail()
    {
        var (_, profile, vm) = Build();

        profile.TrySelectByInternalName("Sword").Should().BeTrue();

        vm.SelectedRow!.Kind.Should().Be(TreasureRowKind.Profile);
        vm.DetailViewModel.Should().BeOfType<ProfileDetailViewModel>();
    }

    [Fact]
    public void PowerKindTarget_DoesNotMatchProfileOfSameName()
    {
        // A profile named "Sword" must not be picked up by the Power kind target — the
        // Kind discriminator keeps the two namespaces separate in the unified list.
        var (power, _, vm) = Build();
        power.TrySelectByInternalName("Sword").Should().BeFalse();
        vm.SelectedRow.Should().BeNull();
    }

    [Fact]
    public void TrySelect_Unknown_ReturnsFalse_VmUnchanged()
    {
        var (power, profile, vm) = Build();
        power.TrySelectByInternalName("Nope").Should().BeFalse();
        profile.TrySelectByInternalName("Nope").Should().BeFalse();
        vm.SelectedRow.Should().BeNull();
    }

    [Fact]
    public void TrySelect_ClearsResidualQueryText()
    {
        var (power, _, vm) = Build();
        vm.QueryText = "KindLabel = 'Power'";

        power.TrySelectByInternalName("SwordBoost").Should().BeTrue();

        vm.QueryText.Should().BeEmpty();
    }

    [Fact]
    public void TryOpenInWindow_NoDetail_ReturnsFalse()
    {
        var (power, profile, _) = Build();
        power.TryOpenInWindow().Should().BeFalse();
        profile.TryOpenInWindow().Should().BeFalse();
    }
}
