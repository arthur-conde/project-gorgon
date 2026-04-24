using FluentAssertions;
using Gorgon.Shared.Modules;
using Gorgon.Shared.Wpf;
using Xunit;

namespace Gorgon.Shared.Tests.Modules;

public class DeepLinkRouterTests
{
    [Fact]
    public void ItemUri_Dispatched_WithInternalName()
    {
        var presenter = new RecordingPresenter();
        var router = new DeepLinkRouter(presenter);

        var handled = router.Handle("gorgon://item/CraftedLeatherBoots5");

        handled.Should().BeTrue();
        presenter.LastShown.Should().Be("CraftedLeatherBoots5");
    }

    [Fact]
    public void SchemeIsCaseInsensitive()
    {
        var presenter = new RecordingPresenter();
        var router = new DeepLinkRouter(presenter);

        router.Handle("GORGON://item/CraftedLeatherBoots5").Should().BeTrue();
        presenter.LastShown.Should().Be("CraftedLeatherBoots5");
    }

    [Theory]
    [InlineData("http://item/CraftedLeatherBoots5")]      // wrong scheme
    [InlineData("gorgon://recipe/Bread")]                 // unknown action
    [InlineData("gorgon://item/")]                        // empty payload
    [InlineData("gorgon://item/has space")]               // illegal chars in payload
    [InlineData("gorgon://item/has-hyphen")]              // hyphen not in [A-Za-z0-9_]
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
        router.Handle($"gorgon://item/{tooLong}").Should().BeFalse();
        presenter.LastShown.Should().BeNull();
    }

    [Fact]
    public void Payload_AtLengthCap_IsAccepted()
    {
        var presenter = new RecordingPresenter();
        var router = new DeepLinkRouter(presenter);

        var cap = new string('A', 128);
        router.Handle($"gorgon://item/{cap}").Should().BeTrue();
        presenter.LastShown.Should().Be(cap);
    }

    // ---- gorgon://list/… branch --------------------------------------------

    [Fact]
    public void ListUri_Dispatched_WithBase64UrlPayload()
    {
        var presenter = new RecordingPresenter();
        var listTarget = new RecordingListTarget();
        var router = new DeepLinkRouter(presenter, listTarget);

        // Base64url alphabet includes '-' and '_', which the item validator rejects — this
        // also confirms per-action validation routes here instead of the stricter item rule.
        var handled = router.Handle("gorgon://list/AB-_abcXYZ123");

        handled.Should().BeTrue();
        listTarget.LastPayload.Should().Be("AB-_abcXYZ123");
    }

    [Fact]
    public void ListUri_WithoutRegisteredTarget_IsDropped()
    {
        var presenter = new RecordingPresenter();
        var router = new DeepLinkRouter(presenter, listImport: null);

        router.Handle("gorgon://list/AB-_abcXYZ123").Should().BeFalse();
    }

    [Theory]
    [InlineData("gorgon://list/has.dot")]    // '.' not in base64url alphabet
    [InlineData("gorgon://list/has space")]  // spaces illegal
    [InlineData("gorgon://list/")]           // empty payload
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
        router.Handle($"gorgon://list/{tooLong}").Should().BeFalse();
        listTarget.LastPayload.Should().BeNull();
    }

    private sealed class RecordingPresenter : IItemDetailPresenter
    {
        public string? LastShown { get; private set; }
        public void Show(string internalName) => LastShown = internalName;
    }

    private sealed class RecordingListTarget : ICraftListImportTarget
    {
        public string? LastPayload { get; private set; }
        public void ImportFromLinkPayload(string base64UrlPayload) => LastPayload = base64UrlPayload;
    }
}
