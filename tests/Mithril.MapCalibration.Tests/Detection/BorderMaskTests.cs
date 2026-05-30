using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public class BorderMaskTests
{
    private static byte[] Bgra(int w, int h, Func<int, int, (byte R, byte G, byte B)> px)
    {
        var buf = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            var (r, g, b) = px(x, y);
            buf[i] = b; buf[i + 1] = g; buf[i + 2] = r; buf[i + 3] = 255;
        }
        return buf;
    }

    private static readonly (byte, byte, byte) Green = (40, 180, 40);
    private static readonly (byte, byte, byte) Rock = (130, 130, 130);

    [Fact]
    public void Masks_edge_connected_rock_but_not_the_green_interior()
    {
        const int w = 40, h = 40;
        var bgra = Bgra(w, h, (x, y) => (x is >= 10 and < 30 && y is >= 10 and < 30) ? Green : Rock);

        var mask = BorderMask.Compute(bgra, w, h, step: 1);

        mask[5 * w + 5].Should().BeTrue("the edge-connected gray rim is border");
        mask[20 * w + 20].Should().BeFalse("the green interior is not border");
    }

    [Fact]
    public void Does_not_leak_through_a_green_ring_into_an_enclosed_gray_pocket()
    {
        // Gray rim, a green ring at 8..32, and a gray pocket inside 14..26. The
        // pocket is gray (fillable) but enclosed by green, so the edge flood can't
        // reach it — an interior stone structure must survive (stay unmasked).
        const int w = 40, h = 40;
        var bgra = Bgra(w, h, (x, y) =>
        {
            var ring = x is >= 8 and < 32 && y is >= 8 and < 32;
            var pocket = x is >= 14 and < 26 && y is >= 14 and < 26;
            return ring && !pocket ? Green : Rock;
        });

        var mask = BorderMask.Compute(bgra, w, h, step: 1);

        mask[2 * w + 2].Should().BeTrue("outer gray rim is border");
        mask[20 * w + 20].Should().BeFalse("the enclosed gray pocket is unreachable from the edge");
    }
}
