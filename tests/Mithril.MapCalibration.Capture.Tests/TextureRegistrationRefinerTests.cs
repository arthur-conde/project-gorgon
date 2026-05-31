using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class TextureRegistrationRefinerTests
{
    [Fact]
    public void Refine_locates_an_embedded_texture()
    {
        var tex = SyntheticMap.NoisyTexture(seed: 3, w: 64, h: 64);
        var frame = SyntheticMap.PasteInto(tex, canvasW: 160, canvasH: 120, atX: 40, atY: 30);
        var rect = new TextureRegistrationRefiner().Refine(frame, tex, minScore: 0.5);
        rect.Should().NotBeNull();
        rect!.OriginX.Should().BeCloseTo(40, 3);
        rect.OriginY.Should().BeCloseTo(30, 3);
    }
}
