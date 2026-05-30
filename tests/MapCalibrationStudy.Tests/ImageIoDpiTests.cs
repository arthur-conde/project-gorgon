using System.Drawing;
using System.Drawing.Imaging;
using FluentAssertions;
using Mithril.Tools.MapCalibration.Common;
using Xunit;

namespace Mithril.Tools.MapCalibrationStudy.Tests;

public class ImageIoDpiTests
{
    // Regression: a screenshot a crop/editor tool saved at non-96 DPI must load
    // at full pixel size, not get shrunk into the top-left corner. The old
    // ReadBgra used DrawImageUnscaled, which honors DPI and draws a 300-DPI
    // image at ~1/3 size with the rest left black — corrupting NCC + debug.
    [Fact]
    public void LoadBgra_does_not_shrink_a_high_dpi_image_into_the_corner()
    {
        var path = Path.Combine(Path.GetTempPath(), $"imageio-dpi-{Guid.NewGuid():N}.png");
        try
        {
            using (var bmp = new Bitmap(40, 40, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp)) g.Clear(Color.White);
                bmp.SetResolution(300, 300);   // the trap: physical size ≠ pixel size
                bmp.Save(path, ImageFormat.Png);
            }

            var (bgra, w, h) = ImageIo.LoadBgra(path);

            w.Should().Be(40);
            h.Should().Be(40);
            // Bottom-right pixel must be the white content, not black background:
            // if the image were drawn at physical (DPI) size it would sit ~13x13
            // in the corner and (38,38) would be black.
            var idx = (38 * w + 38) * 4;
            bgra[idx + 2].Should().BeGreaterThan(200,
                "a high-DPI image must map all its pixels 1:1, not shrink into the corner");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
