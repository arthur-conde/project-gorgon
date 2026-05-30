using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public sealed class ImageOpsTests
{
    [Fact]
    public void Crop_extracts_subrect()
    {
        var px = new byte[4 * 4];
        for (byte i = 0; i < px.Length; i++) px[i] = i;
        var img = new GrayImage(4, 4, px);

        var crop = ImageOps.Crop(img, 1, 1, 2, 2);

        crop.Width.Should().Be(2);
        crop.Pixels.Should().Equal((byte)5, (byte)6, (byte)9, (byte)10);
    }

    [Fact]
    public void Downsample_box_averages()
    {
        var img = new GrayImage(2, 2, new byte[] { 0, 10, 20, 30 });
        ImageOps.Downsample(img, 2).Pixels.Should().Equal((byte)15);
    }
}
