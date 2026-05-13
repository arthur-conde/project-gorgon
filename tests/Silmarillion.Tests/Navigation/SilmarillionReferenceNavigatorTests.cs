using FluentAssertions;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Xunit;

namespace Silmarillion.Tests.Navigation;

public sealed class SilmarillionReferenceNavigatorTests
{
    [Fact]
    public void Initial_CurrentIsNull_AndStacksEmpty()
    {
        var nav = new SilmarillionReferenceNavigator();

        nav.Current.Should().BeNull();
        nav.CanGoBack.Should().BeFalse();
        nav.CanGoForward.Should().BeFalse();
    }

    [Fact]
    public void Open_SetsCurrent_AndFiresNavigatedWithOpenKind()
    {
        var nav = new SilmarillionReferenceNavigator();
        NavigatedEventArgs? captured = null;
        nav.Navigated += (_, e) => captured = e;

        nav.Open(EntityRef.Item("Tomato"));

        nav.Current.Should().Be(EntityRef.Item("Tomato"));
        captured.Should().NotBeNull();
        captured!.Kind.Should().Be(NavigationKind.Open);
        captured.Previous.Should().BeNull();
        captured.Current.Should().Be(EntityRef.Item("Tomato"));
    }

    [Fact]
    public void Open_AfterFirstOpen_PushesPreviousToBackStack()
    {
        var nav = new SilmarillionReferenceNavigator();
        nav.Open(EntityRef.Item("Tomato"));
        nav.Open(EntityRef.Recipe("MakeSalsa"));

        nav.Current.Should().Be(EntityRef.Recipe("MakeSalsa"));
        nav.CanGoBack.Should().BeTrue();
        nav.CanGoForward.Should().BeFalse();
    }

    [Fact]
    public void Open_ClearsForwardStack()
    {
        var nav = new SilmarillionReferenceNavigator();
        nav.Open(EntityRef.Item("A"));
        nav.Open(EntityRef.Item("B"));
        nav.Back();
        nav.CanGoForward.Should().BeTrue();

        nav.Open(EntityRef.Item("C"));

        nav.CanGoForward.Should().BeFalse();
    }

    [Fact]
    public void Back_PopsBackStack_PushesForward_FiresNavigatedWithBackKind()
    {
        var nav = new SilmarillionReferenceNavigator();
        nav.Open(EntityRef.Item("A"));
        nav.Open(EntityRef.Item("B"));
        NavigatedEventArgs? captured = null;
        nav.Navigated += (_, e) => captured = e;

        nav.Back();

        nav.Current.Should().Be(EntityRef.Item("A"));
        nav.CanGoBack.Should().BeFalse();
        nav.CanGoForward.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.Kind.Should().Be(NavigationKind.Back);
        captured.Previous.Should().Be(EntityRef.Item("B"));
        captured.Current.Should().Be(EntityRef.Item("A"));
    }

    [Fact]
    public void Forward_PopsForwardStack_PushesBack_FiresNavigatedWithForwardKind()
    {
        var nav = new SilmarillionReferenceNavigator();
        nav.Open(EntityRef.Item("A"));
        nav.Open(EntityRef.Item("B"));
        nav.Back();
        NavigatedEventArgs? captured = null;
        nav.Navigated += (_, e) => captured = e;

        nav.Forward();

        nav.Current.Should().Be(EntityRef.Item("B"));
        nav.CanGoBack.Should().BeTrue();
        nav.CanGoForward.Should().BeFalse();
        captured.Should().NotBeNull();
        captured!.Kind.Should().Be(NavigationKind.Forward);
    }

    [Fact]
    public void Back_WhenStackEmpty_IsNoOp_AndDoesNotFireNavigated()
    {
        var nav = new SilmarillionReferenceNavigator();
        var fired = false;
        nav.Navigated += (_, _) => fired = true;

        nav.Back();

        fired.Should().BeFalse();
        nav.Current.Should().BeNull();
    }

    [Fact]
    public void Forward_WhenStackEmpty_IsNoOp_AndDoesNotFireNavigated()
    {
        var nav = new SilmarillionReferenceNavigator();
        nav.Open(EntityRef.Item("A"));
        var fired = false;
        nav.Navigated += (_, _) => fired = true;

        nav.Forward();

        fired.Should().BeFalse();
    }

    [Fact]
    public void OpenSameEntityTwice_StillPushesToBackStack()
    {
        // "Open same item again" is still a history step. User can hit Back to undo.
        var nav = new SilmarillionReferenceNavigator();
        nav.Open(EntityRef.Item("A"));
        nav.Open(EntityRef.Item("A"));

        nav.CanGoBack.Should().BeTrue();
    }

    [Theory]
    [InlineData(EntityKind.Item, true)]
    [InlineData(EntityKind.Recipe, true)]
    [InlineData(EntityKind.Ability, false)]
    [InlineData(EntityKind.Npc, false)]
    [InlineData(EntityKind.Quest, false)]
    [InlineData(EntityKind.Lorebook, false)]
    [InlineData(EntityKind.Landmark, false)]
    [InlineData(EntityKind.Area, false)]
    [InlineData(EntityKind.PlayerTitle, false)]
    [InlineData(EntityKind.StorageVault, false)]
    [InlineData(EntityKind.Effect, false)]
    public void CanOpen_TrueForV1TabbedKinds_FalseOtherwise(EntityKind kind, bool expected)
    {
        var nav = new SilmarillionReferenceNavigator();
        nav.CanOpen(new EntityRef(kind, "anything")).Should().Be(expected);
    }

    [Fact]
    public void Back_PreservesDeepHistory_WhenMultipleEntitiesOnStack()
    {
        var nav = new SilmarillionReferenceNavigator();
        nav.Open(EntityRef.Item("A"));
        nav.Open(EntityRef.Recipe("B"));
        nav.Open(EntityRef.Item("C"));
        nav.Open(EntityRef.Recipe("D"));

        nav.Back();
        nav.Back();
        nav.Back();

        nav.Current.Should().Be(EntityRef.Item("A"));
        nav.CanGoBack.Should().BeFalse();
        nav.CanGoForward.Should().BeTrue();
    }

    [Fact]
    public void Forward_AfterMultipleBacks_RestoresIntermediateState()
    {
        var nav = new SilmarillionReferenceNavigator();
        nav.Open(EntityRef.Item("A"));
        nav.Open(EntityRef.Item("B"));
        nav.Open(EntityRef.Item("C"));

        nav.Back();   // C → B
        nav.Back();   // B → A
        nav.Forward();// A → B

        nav.Current.Should().Be(EntityRef.Item("B"));
        nav.CanGoBack.Should().BeTrue();
        nav.CanGoForward.Should().BeTrue();
    }
}
