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

    private sealed class RecordingPresenter : IItemDetailPresenter
    {
        public string? LastShown { get; private set; }
        public void Show(string internalName) => LastShown = internalName;
    }
}
