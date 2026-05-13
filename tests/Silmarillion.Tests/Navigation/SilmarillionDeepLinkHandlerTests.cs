using FluentAssertions;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Xunit;

namespace Silmarillion.Tests.Navigation;

public sealed class SilmarillionDeepLinkHandlerTests
{
    [Fact]
    public void TryHandle_ItemKind_OpensItem()
    {
        var nav = new RecordingNavigator();
        var handler = new SilmarillionDeepLinkHandler(nav);

        var handled = handler.TryHandle("item/CraftedLeatherBoots5", diag: null);

        handled.Should().BeTrue();
        nav.LastOpened.Should().Be(EntityRef.Item("CraftedLeatherBoots5"));
    }

    [Fact]
    public void TryHandle_RecipeKind_OpensRecipe()
    {
        var nav = new RecordingNavigator();
        var handler = new SilmarillionDeepLinkHandler(nav);

        var handled = handler.TryHandle("recipe/MakeTomatoSauce", diag: null);

        handled.Should().BeTrue();
        nav.LastOpened.Should().Be(EntityRef.Recipe("MakeTomatoSauce"));
    }

    [Theory]
    [InlineData("npc/Marna")]            // unknown kind segment
    [InlineData("ability/Hatchet")]
    [InlineData("")]                     // empty subPath
    [InlineData("item")]                 // no second segment
    [InlineData("item/")]                // empty payload after slash
    [InlineData("item/has space")]       // illegal payload char
    [InlineData("item/has-hyphen")]      // hyphen rejected by EntityPayloadPattern
    [InlineData("item/extra/segment")]   // extra path segments forbidden
    public void TryHandle_Malformed_ReturnsFalse_AndDoesNotDispatch(string subPath)
    {
        var nav = new RecordingNavigator();
        var handler = new SilmarillionDeepLinkHandler(nav);

        handler.TryHandle(subPath, diag: null).Should().BeFalse();
        nav.LastOpened.Should().BeNull();
    }

    [Fact]
    public void TryHandle_PayloadOverLengthCap_IsRejected()
    {
        var nav = new RecordingNavigator();
        var handler = new SilmarillionDeepLinkHandler(nav);

        var tooLong = new string('A', 129);
        handler.TryHandle($"item/{tooLong}", diag: null).Should().BeFalse();
        nav.LastOpened.Should().BeNull();
    }

    [Fact]
    public void Action_IsSilmarillion()
    {
        var nav = new RecordingNavigator();
        new SilmarillionDeepLinkHandler(nav).Action.Should().Be("silmarillion");
    }

    [Theory]
    [InlineData("ITEM/CraftedLeatherBoots5")]
    [InlineData("Item/CraftedLeatherBoots5")]
    [InlineData("iTeM/CraftedLeatherBoots5")]
    public void TryHandle_KindIsCaseInsensitive(string subPath)
    {
        var nav = new RecordingNavigator();
        var handler = new SilmarillionDeepLinkHandler(nav);

        handler.TryHandle(subPath, diag: null).Should().BeTrue();
        nav.LastOpened.Should().Be(EntityRef.Item("CraftedLeatherBoots5"));
    }

    private sealed class RecordingNavigator : IReferenceNavigator
    {
        public EntityRef? LastOpened { get; private set; }
        public EntityRef? Current => LastOpened;
        public bool CanGoBack => false;
        public bool CanGoForward => false;
        public bool CanOpen(EntityRef reference) => true;
        public void Open(EntityRef reference) => LastOpened = reference;
        public void Back() { }
        public void Forward() { }
        public event EventHandler<NavigatedEventArgs>? Navigated { add { } remove { } }
    }
}
