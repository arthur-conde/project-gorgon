using FluentAssertions;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using Xunit;

namespace Mithril.Shared.Tests.Modules;

public class DeepLinkRouterTests
{
    [Fact]
    public void ItemUri_Dispatched_WithInternalName()
    {
        var presenter = new RecordingPresenter();
        var router = new DeepLinkRouter(presenter);

        var handled = router.Handle("mithril://item/CraftedLeatherBoots5");

        handled.Should().BeTrue();
        presenter.LastShown.Should().Be("CraftedLeatherBoots5");
    }

    [Fact]
    public void SchemeIsCaseInsensitive()
    {
        var presenter = new RecordingPresenter();
        var router = new DeepLinkRouter(presenter);

        router.Handle("mithril://item/CraftedLeatherBoots5").Should().BeTrue();
        presenter.LastShown.Should().Be("CraftedLeatherBoots5");
    }

    [Theory]
    [InlineData("http://item/CraftedLeatherBoots5")]      // wrong scheme
    [InlineData("mithril://recipe/Bread")]                 // unknown action
    [InlineData("mithril://item/")]                        // empty payload
    [InlineData("mithril://item/has space")]               // illegal chars in payload
    [InlineData("mithril://item/has-hyphen")]              // hyphen not in [A-Za-z0-9_]
    [InlineData("not a uri at all")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectedInputs_ReturnFalse_AndDontDispatch(string? uri)
    {
        var presenter = new RecordingPresenter();
        var router = new DeepLinkRouter(presenter);

        router.Handle(uri).Should().BeFalse();
        presenter.LastShown.Should().BeNull();
    }

    [Fact]
    public void Payload_OverLengthCap_IsRejected()
    {
        var presenter = new RecordingPresenter();
        var router = new DeepLinkRouter(presenter);

        var tooLong = new string('A', 129);
        router.Handle($"mithril://item/{tooLong}").Should().BeFalse();
        presenter.LastShown.Should().BeNull();
    }

    [Fact]
    public void Payload_AtLengthCap_IsAccepted()
    {
        var presenter = new RecordingPresenter();
        var router = new DeepLinkRouter(presenter);

        var cap = new string('A', 128);
        router.Handle($"mithril://item/{cap}").Should().BeTrue();
        presenter.LastShown.Should().Be(cap);
    }

    // ---- mithril://list/… branch --------------------------------------------

    [Fact]
    public void ListUri_Dispatched_WithBase64UrlPayload()
    {
        var presenter = new RecordingPresenter();
        var listTarget = new RecordingListTarget();
        var router = new DeepLinkRouter(presenter, listTarget);

        // Base64url alphabet includes '-' and '_', which the item validator rejects — this
        // also confirms per-action validation routes here instead of the stricter item rule.
        var handled = router.Handle("mithril://list/AB-_abcXYZ123");

        handled.Should().BeTrue();
        listTarget.LastPayload.Should().Be("AB-_abcXYZ123");
    }

    [Fact]
    public void ListUri_WithoutRegisteredTarget_IsDropped()
    {
        var presenter = new RecordingPresenter();
        var router = new DeepLinkRouter(presenter, listImport: null);

        router.Handle("mithril://list/AB-_abcXYZ123").Should().BeFalse();
    }

    [Theory]
    [InlineData("mithril://list/has.dot")]    // '.' not in base64url alphabet
    [InlineData("mithril://list/has space")]  // spaces illegal
    [InlineData("mithril://list/")]           // empty payload
    public void ListUri_MalformedPayload_IsRejected(string uri)
    {
        var presenter = new RecordingPresenter();
        var listTarget = new RecordingListTarget();
        var router = new DeepLinkRouter(presenter, listTarget);

        router.Handle(uri).Should().BeFalse();
        listTarget.LastPayload.Should().BeNull();
    }

    [Fact]
    public void ListUri_PayloadOverLengthCap_IsRejected()
    {
        var presenter = new RecordingPresenter();
        var listTarget = new RecordingListTarget();
        var router = new DeepLinkRouter(presenter, listTarget);

        var tooLong = new string('A', 8193);
        router.Handle($"mithril://list/{tooLong}").Should().BeFalse();
        listTarget.LastPayload.Should().BeNull();
    }

    private sealed class RecordingPresenter : IItemDetailPresenter
    {
        public string? LastShown { get; private set; }
        public void Show(string internalName) => LastShown = internalName;
        public void Show(string internalName, IReadOnlyList<AugmentPreview> augments) => LastShown = internalName;
    }

    private sealed class RecordingListTarget : ICraftListImportTarget
    {
        public string? LastPayload { get; private set; }
        public void ImportFromLinkPayload(string base64UrlPayload) => LastPayload = base64UrlPayload;
    }
}
