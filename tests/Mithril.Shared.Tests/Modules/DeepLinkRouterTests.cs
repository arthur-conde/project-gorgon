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
        var router = new DeepLinkRouter(nav);

        var handled = router.Handle("mithril://item/CraftedLeatherBoots5");

        handled.Should().BeTrue();
        nav.LastOpened.Should().Be(EntityRef.Item("CraftedLeatherBoots5"));
    }

    [Fact]
    public void RecipeUri_DispatchedTo_ReferenceNavigator_AsRecipeKind()
    {
        var nav = new RecordingNavigator();
        var router = new DeepLinkRouter(nav);

        var handled = router.Handle("mithril://recipe/MakeTomatoSauce");

        handled.Should().BeTrue();
        nav.LastOpened.Should().Be(EntityRef.Recipe("MakeTomatoSauce"));
    }

    [Fact]
    public void SchemeIsCaseInsensitive()
    {
        var nav = new RecordingNavigator();
        var router = new DeepLinkRouter(nav);

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
        var router = new DeepLinkRouter(nav);

        router.Handle(uri).Should().BeFalse();
        nav.LastOpened.Should().BeNull();
    }

    [Fact]
    public void Payload_OverLengthCap_IsRejected()
    {
        var nav = new RecordingNavigator();
        var router = new DeepLinkRouter(nav);

        var tooLong = new string('A', 129);
        router.Handle($"mithril://item/{tooLong}").Should().BeFalse();
        nav.LastOpened.Should().BeNull();
    }

    [Fact]
    public void Payload_AtLengthCap_IsAccepted()
    {
        var nav = new RecordingNavigator();
        var router = new DeepLinkRouter(nav);

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
        var router = new DeepLinkRouter(nav, listTarget);

        // Base64url alphabet includes '-' and '_', which the item validator rejects — this
        // also confirms per-action validation routes here instead of the stricter item rule.
        var handled = router.Handle("mithril://list/AB-_abcXYZ123");

        handled.Should().BeTrue();
        listTarget.LastPayload.Should().Be("AB-_abcXYZ123");
    }

    [Fact]
    public void ListUri_WithoutRegisteredTarget_IsDropped()
    {
        var nav = new RecordingNavigator();
        var router = new DeepLinkRouter(nav, listImport: null);

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
        var router = new DeepLinkRouter(nav, listTarget);

        router.Handle(uri).Should().BeFalse();
        listTarget.LastPayload.Should().BeNull();
    }

    [Fact]
    public void ListUri_PayloadOverLengthCap_IsRejected()
    {
        var nav = new RecordingNavigator();
        var listTarget = new RecordingListTarget();
        var router = new DeepLinkRouter(nav, listTarget);

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
        var router = new DeepLinkRouter(nav, listImport: null, pippinImport: pippinTarget);

        var handled = router.Handle("mithril://pippin/AB-_abcXYZ123");

        handled.Should().BeTrue();
        pippinTarget.LastPayload.Should().Be("AB-_abcXYZ123");
    }

    [Fact]
    public void PippinUri_WithoutRegisteredTarget_IsDropped()
    {
        var nav = new RecordingNavigator();
        var router = new DeepLinkRouter(nav, listImport: null, pippinImport: null);

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
        var router = new DeepLinkRouter(nav, listImport: null, pippinImport: pippinTarget);

        router.Handle(uri).Should().BeFalse();
        pippinTarget.LastPayload.Should().BeNull();
    }

    [Fact]
    public void PippinUri_PayloadOverLengthCap_IsRejected()
    {
        var nav = new RecordingNavigator();
        var pippinTarget = new RecordingPippinTarget();
        var router = new DeepLinkRouter(nav, listImport: null, pippinImport: pippinTarget);

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
        var router = new DeepLinkRouter(nav, listImport: null, pippinImport: null, legolasImport: legolasTarget);

        var handled = router.Handle("mithril://legolas/AB-_abcXYZ123");

        handled.Should().BeTrue();
        legolasTarget.LastPayload.Should().Be("AB-_abcXYZ123");
    }

    [Fact]
    public void LegolasUri_WithoutRegisteredTarget_IsDropped()
    {
        var nav = new RecordingNavigator();
        var router = new DeepLinkRouter(nav, listImport: null, pippinImport: null, legolasImport: null);

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
        var router = new DeepLinkRouter(nav, listImport: null, pippinImport: null, legolasImport: legolasTarget);

        router.Handle(uri).Should().BeFalse();
        legolasTarget.LastPayload.Should().BeNull();
    }

    [Fact]
    public void LegolasUri_PayloadOverLengthCap_IsRejected()
    {
        var nav = new RecordingNavigator();
        var legolasTarget = new RecordingLegolasTarget();
        var router = new DeepLinkRouter(nav, listImport: null, pippinImport: null, legolasImport: legolasTarget);

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
        var router = new DeepLinkRouter(nav, listImport: null, pippinImport: null,
            legolasImport: null, elrondImport: elrondTarget);

        var handled = router.Handle("mithril://elrond/ArmorAugmentBrewing");

        handled.Should().BeTrue();
        elrondTarget.LastPayload.Should().Be("ArmorAugmentBrewing");
    }

    [Fact]
    public void ElrondUri_WithoutRegisteredTarget_IsDropped()
    {
        var nav = new RecordingNavigator();
        var router = new DeepLinkRouter(nav, listImport: null, pippinImport: null,
            legolasImport: null, elrondImport: null);

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
        var router = new DeepLinkRouter(nav, listImport: null, pippinImport: null,
            legolasImport: null, elrondImport: elrondTarget);

        router.Handle(uri).Should().BeFalse();
        elrondTarget.LastPayload.Should().BeNull();
    }

    [Fact]
    public void ElrondUri_PayloadOverLengthCap_IsRejected()
    {
        var nav = new RecordingNavigator();
        var elrondTarget = new RecordingElrondTarget();
        var router = new DeepLinkRouter(nav, listImport: null, pippinImport: null,
            legolasImport: null, elrondImport: elrondTarget);

        var tooLong = new string('A', 129);
        router.Handle($"mithril://elrond/{tooLong}").Should().BeFalse();
        elrondTarget.LastPayload.Should().BeNull();
    }

    private sealed class RecordingElrondTarget : IElrondSkillImportTarget
    {
        public string? LastPayload { get; private set; }
        public void ImportFromLinkPayload(string skillKey) => LastPayload = skillKey;
    }
}
