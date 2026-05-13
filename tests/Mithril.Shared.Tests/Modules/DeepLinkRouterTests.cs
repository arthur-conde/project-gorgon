using FluentAssertions;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Modules;

public class DeepLinkRouterTests
{
    [Fact]
    public void ItemUri_DispatchedTo_ReferenceNavigator_AsItemKind()
    {
        var nav = new RecordingNavigator();
        var router = BuildRouter(nav);

        var handled = router.Handle("mithril://item/CraftedLeatherBoots5");

        handled.Should().BeTrue();
        nav.LastOpened.Should().Be(EntityRef.Item("CraftedLeatherBoots5"));
    }

    [Fact]
    public void RecipeUri_DispatchedTo_ReferenceNavigator_AsRecipeKind()
    {
        var nav = new RecordingNavigator();
        var router = BuildRouter(nav);

        var handled = router.Handle("mithril://recipe/MakeTomatoSauce");

        handled.Should().BeTrue();
        nav.LastOpened.Should().Be(EntityRef.Recipe("MakeTomatoSauce"));
    }

    [Fact]
    public void SchemeIsCaseInsensitive()
    {
        var nav = new RecordingNavigator();
        var router = BuildRouter(nav);

        router.Handle("MITHRIL://item/CraftedLeatherBoots5").Should().BeTrue();
        nav.LastOpened.Should().Be(EntityRef.Item("CraftedLeatherBoots5"));
    }

    [Theory]
    [InlineData("http://item/CraftedLeatherBoots5")]      // wrong scheme
    [InlineData("mithril://gibberish/Bread")]              // unknown action
    [InlineData("mithril://item/")]                        // empty payload
    [InlineData("mithril://item/has space")]               // illegal chars in payload
    [InlineData("mithril://item/has-hyphen")]              // hyphen not in [A-Za-z0-9_]
    [InlineData("mithril://recipe/")]                      // empty recipe payload
    [InlineData("mithril://recipe/has space")]
    [InlineData("not a uri at all")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectedInputs_ReturnFalse_AndDontDispatch(string? uri)
    {
        var nav = new RecordingNavigator();
        var router = BuildRouter(nav);

        router.Handle(uri).Should().BeFalse();
        nav.LastOpened.Should().BeNull();
    }

    [Fact]
    public void Payload_OverLengthCap_IsRejected()
    {
        var nav = new RecordingNavigator();
        var router = BuildRouter(nav);

        var tooLong = new string('A', 129);
        router.Handle($"mithril://item/{tooLong}").Should().BeFalse();
        nav.LastOpened.Should().BeNull();
    }

    [Fact]
    public void Payload_AtLengthCap_IsAccepted()
    {
        var nav = new RecordingNavigator();
        var router = BuildRouter(nav);

        var cap = new string('A', 128);
        router.Handle($"mithril://item/{cap}").Should().BeTrue();
        nav.LastOpened.Should().Be(EntityRef.Item(cap));
    }

    // ---- mithril://list/… branch --------------------------------------------

    [Fact]
    public void ListUri_Dispatched_WithBase64UrlPayload()
    {
        var nav = new RecordingNavigator();
        var listTarget = new RecordingListTarget();
        var router = BuildRouter(nav, listTarget: listTarget);

        // Base64url alphabet includes '-' and '_', which the item validator rejects — this
        // also confirms per-action validation routes here instead of the stricter item rule.
        var handled = router.Handle("mithril://list/AB-_abcXYZ123");

        handled.Should().BeTrue();
        listTarget.LastPayload.Should().Be("AB-_abcXYZ123");
    }

    [Fact]
    public void ListUri_HandlerNotRegistered_IsDropped()
    {
        var nav = new RecordingNavigator();
        var router = BuildRouter(nav, listTarget: null);

        router.Handle("mithril://list/AB-_abcXYZ123").Should().BeFalse();
    }

    [Theory]
    [InlineData("mithril://list/has.dot")]    // '.' not in base64url alphabet
    [InlineData("mithril://list/has space")]  // spaces illegal
    [InlineData("mithril://list/")]           // empty payload
    public void ListUri_MalformedPayload_IsRejected(string uri)
    {
        var nav = new RecordingNavigator();
        var listTarget = new RecordingListTarget();
        var router = BuildRouter(nav, listTarget: listTarget);

        router.Handle(uri).Should().BeFalse();
        listTarget.LastPayload.Should().BeNull();
    }

    [Fact]
    public void ListUri_PayloadOverLengthCap_IsRejected()
    {
        var nav = new RecordingNavigator();
        var listTarget = new RecordingListTarget();
        var router = BuildRouter(nav, listTarget: listTarget);

        var tooLong = new string('A', 8193);
        router.Handle($"mithril://list/{tooLong}").Should().BeFalse();
        listTarget.LastPayload.Should().BeNull();
    }

    // ---- mithril://pippin/… branch ------------------------------------------

    [Fact]
    public void PippinUri_Dispatched_WithBase64UrlPayload()
    {
        var nav = new RecordingNavigator();
        var pippinTarget = new RecordingPippinTarget();
        var router = BuildRouter(nav, pippinTarget: pippinTarget);

        var handled = router.Handle("mithril://pippin/AB-_abcXYZ123");

        handled.Should().BeTrue();
        pippinTarget.LastPayload.Should().Be("AB-_abcXYZ123");
    }

    [Fact]
    public void PippinUri_HandlerNotRegistered_IsDropped()
    {
        var nav = new RecordingNavigator();
        var router = BuildRouter(nav, pippinTarget: null);

        router.Handle("mithril://pippin/AB-_abcXYZ123").Should().BeFalse();
    }

    [Theory]
    [InlineData("mithril://pippin/has.dot")]
    [InlineData("mithril://pippin/has space")]
    [InlineData("mithril://pippin/")]
    public void PippinUri_MalformedPayload_IsRejected(string uri)
    {
        var nav = new RecordingNavigator();
        var pippinTarget = new RecordingPippinTarget();
        var router = BuildRouter(nav, pippinTarget: pippinTarget);

        router.Handle(uri).Should().BeFalse();
        pippinTarget.LastPayload.Should().BeNull();
    }

    [Fact]
    public void PippinUri_PayloadOverLengthCap_IsRejected()
    {
        var nav = new RecordingNavigator();
        var pippinTarget = new RecordingPippinTarget();
        var router = BuildRouter(nav, pippinTarget: pippinTarget);

        var tooLong = new string('A', 16_385);
        router.Handle($"mithril://pippin/{tooLong}").Should().BeFalse();
        pippinTarget.LastPayload.Should().BeNull();
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

    private sealed class RecordingListTarget : ICraftListImportTarget
    {
        public string? LastPayload { get; private set; }
        public void ImportFromLinkPayload(string base64UrlPayload) => LastPayload = base64UrlPayload;
    }

    private sealed class RecordingPippinTarget : IPippinShareImportTarget
    {
        public string? LastPayload { get; private set; }
        public void ImportFromLinkPayload(string base64UrlPayload) => LastPayload = base64UrlPayload;
    }

    // ---- mithril://legolas/… branch -----------------------------------------

    [Fact]
    public void LegolasUri_Dispatched_WithBase64UrlPayload()
    {
        var nav = new RecordingNavigator();
        var legolasTarget = new RecordingLegolasTarget();
        var router = BuildRouter(nav, legolasTarget: legolasTarget);

        var handled = router.Handle("mithril://legolas/AB-_abcXYZ123");

        handled.Should().BeTrue();
        legolasTarget.LastPayload.Should().Be("AB-_abcXYZ123");
    }

    [Fact]
    public void LegolasUri_HandlerNotRegistered_IsDropped()
    {
        var nav = new RecordingNavigator();
        var router = BuildRouter(nav, legolasTarget: null);

        router.Handle("mithril://legolas/AB-_abcXYZ123").Should().BeFalse();
    }

    [Theory]
    [InlineData("mithril://legolas/has.dot")]
    [InlineData("mithril://legolas/has space")]
    [InlineData("mithril://legolas/")]
    public void LegolasUri_MalformedPayload_IsRejected(string uri)
    {
        var nav = new RecordingNavigator();
        var legolasTarget = new RecordingLegolasTarget();
        var router = BuildRouter(nav, legolasTarget: legolasTarget);

        router.Handle(uri).Should().BeFalse();
        legolasTarget.LastPayload.Should().BeNull();
    }

    [Fact]
    public void LegolasUri_PayloadOverLengthCap_IsRejected()
    {
        var nav = new RecordingNavigator();
        var legolasTarget = new RecordingLegolasTarget();
        var router = BuildRouter(nav, legolasTarget: legolasTarget);

        var tooLong = new string('A', 8193);
        router.Handle($"mithril://legolas/{tooLong}").Should().BeFalse();
        legolasTarget.LastPayload.Should().BeNull();
    }

    private sealed class RecordingLegolasTarget : ILegolasShareImportTarget
    {
        public string? LastPayload { get; private set; }
        public void ImportFromLinkPayload(string base64UrlPayload) => LastPayload = base64UrlPayload;
    }

    // ---- mithril://elrond/… branch ------------------------------------------

    [Fact]
    public void ElrondUri_Dispatched_WithSkillKey()
    {
        var nav = new RecordingNavigator();
        var elrondTarget = new RecordingElrondTarget();
        var router = BuildRouter(nav, elrondTarget: elrondTarget);

        var handled = router.Handle("mithril://elrond/ArmorAugmentBrewing");

        handled.Should().BeTrue();
        elrondTarget.LastPayload.Should().Be("ArmorAugmentBrewing");
    }

    [Fact]
    public void ElrondUri_HandlerNotRegistered_IsDropped()
    {
        var nav = new RecordingNavigator();
        var router = BuildRouter(nav, elrondTarget: null);

        router.Handle("mithril://elrond/Cooking").Should().BeFalse();
    }

    [Theory]
    [InlineData("mithril://elrond/has-hyphen")]   // hyphen — display names allowed but not on the wire
    [InlineData("mithril://elrond/has space")]
    [InlineData("mithril://elrond/")]
    [InlineData("mithril://elrond/has.dot")]
    public void ElrondUri_MalformedPayload_IsRejected(string uri)
    {
        var nav = new RecordingNavigator();
        var elrondTarget = new RecordingElrondTarget();
        var router = BuildRouter(nav, elrondTarget: elrondTarget);

        router.Handle(uri).Should().BeFalse();
        elrondTarget.LastPayload.Should().BeNull();
    }

    [Fact]
    public void ElrondUri_PayloadOverLengthCap_IsRejected()
    {
        var nav = new RecordingNavigator();
        var elrondTarget = new RecordingElrondTarget();
        var router = BuildRouter(nav, elrondTarget: elrondTarget);

        var tooLong = new string('A', 129);
        router.Handle($"mithril://elrond/{tooLong}").Should().BeFalse();
        elrondTarget.LastPayload.Should().BeNull();
    }

    private sealed class RecordingElrondTarget : IElrondSkillImportTarget
    {
        public string? LastPayload { get; private set; }
        public void ImportFromLinkPayload(string skillKey) => LastPayload = skillKey;
    }

    private static DeepLinkRouter BuildRouter(
        IReferenceNavigator nav,
        ICraftListImportTarget? listTarget = null,
        IPippinShareImportTarget? pippinTarget = null,
        ILegolasShareImportTarget? legolasTarget = null,
        IElrondSkillImportTarget? elrondTarget = null)
    {
        var handlers = new List<IDeepLinkHandler>
        {
            new ItemDeepLinkHandler(nav),
            new RecipeDeepLinkHandler(nav),
        };
        if (listTarget is not null)
            handlers.Add(new Celebrimbor.Services.CraftListDeepLinkHandler(listTarget));
        if (pippinTarget is not null)
            handlers.Add(new Pippin.Sharing.PippinDeepLinkHandler(pippinTarget));
        if (legolasTarget is not null)
            handlers.Add(new Legolas.Sharing.LegolasDeepLinkHandler(legolasTarget));
        if (elrondTarget is not null)
            handlers.Add(new Elrond.Services.ElrondDeepLinkHandler(elrondTarget));
        return new DeepLinkRouter(handlers);
    }

    [Fact]
    public void Constructor_DuplicateAction_Throws()
    {
        var nav = new RecordingNavigator();
        var handlers = new IDeepLinkHandler[]
        {
            new ItemDeepLinkHandler(nav),
            new ItemDeepLinkHandler(nav),  // duplicate
        };
        var act = () => new DeepLinkRouter(handlers);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*Duplicate IDeepLinkHandler*item*");
    }

    [Fact]
    public void SilmarillionRoute_DispatchedToReferenceNavigator()
    {
        var nav = new RecordingNavigator();
        var router = BuildSilmarillionRouter(nav);

        router.Handle("mithril://silmarillion/item/CraftedLeatherBoots5").Should().BeTrue();
        nav.LastOpened.Should().Be(EntityRef.Item("CraftedLeatherBoots5"));

        router.Handle("mithril://silmarillion/recipe/MakeTomatoSauce").Should().BeTrue();
        nav.LastOpened.Should().Be(EntityRef.Recipe("MakeTomatoSauce"));
    }

    [Theory]
    [InlineData("mithril://silmarillion/")]                       // empty payload
    [InlineData("mithril://silmarillion/item")]                   // missing name segment
    [InlineData("mithril://silmarillion/item/")]                  // trailing slash, empty name
    [InlineData("mithril://silmarillion/item/has space")]         // illegal payload char
    [InlineData("mithril://silmarillion/item/has-hyphen")]        // hyphen not in [A-Za-z0-9_]
    [InlineData("mithril://silmarillion/item/extra/segment")]     // extra path segments forbidden
    [InlineData("mithril://silmarillion/garbage/Foo")]            // unknown EntityKind
    public void SilmarillionRoute_MalformedInputs_ReturnFalse_AndDontDispatch(string uri)
    {
        var nav = new RecordingNavigator();
        var router = BuildSilmarillionRouter(nav);

        router.Handle(uri).Should().BeFalse();
        nav.LastOpened.Should().BeNull();
    }

    [Fact]
    public void SilmarillionRoute_PayloadOverLengthCap_IsRejected()
    {
        var nav = new RecordingNavigator();
        var router = BuildSilmarillionRouter(nav);

        var tooLong = new string('A', 129);
        router.Handle($"mithril://silmarillion/item/{tooLong}").Should().BeFalse();
        nav.LastOpened.Should().BeNull();
    }

    private static DeepLinkRouter BuildSilmarillionRouter(IReferenceNavigator nav) =>
        new(new IDeepLinkHandler[]
        {
            new ItemDeepLinkHandler(nav),
            new RecipeDeepLinkHandler(nav),
            new Silmarillion.Navigation.SilmarillionDeepLinkHandler(nav),
        });
}
