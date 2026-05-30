using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public sealed class DeviationFloodRimTests
{
    // Synthetic deviation map: a compact HIGH-deviation icon-sized patch touching
    // the image edge (a "rim" candidate — edge-connected) + an isolated
    // HIGH-deviation icon-sized island in the centre, everything else LOW
    // (matched terrain). Both patches are compact/high-solidity so both pass the
    // shape filter on their own — the ONLY thing that drops the edge patch is the
    // deviation-flood rim mask, which makes the test's mask isolation clean.
    private static float[] EdgePatchPlusIsland(int w, int h)
    {
        var dev = new float[w * h];
        void Fill(int cx, int cy) { for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++) dev[(cy + dy) * w + (cx + dx)] = 1f; }
        // Edge-touching patch in the top-left corner: its 5x5 box includes column 0.
        Fill(2, 20);
        // Interior island, well away from any edge.
        Fill(20, 20);
        return dev;
    }

    [Fact]
    public void DeviationFlood_drops_edge_ring_keeps_interior_island()
    {
        const int w = 40, h = 40;
        var dev = EdgePatchPlusIsland(w, h);

        var opts = new BlobOptions(MinArea: 4, MaxIconArea: 900, MinSolidity: 0.3, MaxAspect: 3.0, MinPeak: 0.7);
        var icons = DeviationBlobDetector.DetectIconBlobs(dev, w, h, lowNcc: 0.5, RimMaskMode.DeviationFlood, opts, closeRadius: 0);

        icons.Should().ContainSingle();                          // only the island survives
        icons[0].Cx.Should().BeApproximately(20, 1.5);
        icons[0].Cy.Should().BeApproximately(20, 1.5);
    }

    [Fact]
    public void No_rim_mask_keeps_the_edge_ring_as_blobs()      // proves the mask is load-bearing
    {
        const int w = 40, h = 40;
        var dev = EdgePatchPlusIsland(w, h);

        var opts = new BlobOptions(MinArea: 4, MaxIconArea: 900, MinSolidity: 0.3, MaxAspect: 3.0, MinPeak: 0.7);
        var icons = DeviationBlobDetector.DetectIconBlobs(dev, w, h, lowNcc: 0.5, RimMaskMode.None, opts, closeRadius: 0);

        // With no rim mask the edge-touching patch is NOT dropped, so both the
        // interior island and the edge patch survive the shape filter.
        icons.Should().HaveCount(2);
    }
}
