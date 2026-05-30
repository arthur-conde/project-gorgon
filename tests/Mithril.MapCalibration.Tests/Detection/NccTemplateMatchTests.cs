using System;
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public sealed class NccTemplateMatchTests
{
    [Fact]
    public void Self_match_scores_near_one()           // §12 positive control
    {
        var rng = new Random(7);
        var px = new byte[40 * 40];
        rng.NextBytes(px);                              // high-variance so NCC is defined
        var img = new GrayImage(40, 40, px);
        var tpl = ImageOps.Crop(img, 12, 12, 12, 12);

        var best = NccTemplateMatch.FindBest(img, tpl, templateMask: null, minScore: 0.5);

        best.Should().NotBeNull();
        best!.Value.Score.Should().BeGreaterThan(0.99);
        best.Value.X.Should().Be(12);
        best.Value.Y.Should().Be(12);
    }

    [Fact]
    public void No_match_on_flat_image_returns_empty()  // negative: featureless → undefined NCC
    {
        var img = new GrayImage(40, 40, new byte[40 * 40]);   // all zero
        var tpl = new GrayImage(8, 8, new byte[64]);
        NccTemplateMatch.FindAll(img, tpl, null, 0.5).Should().BeEmpty();
    }
}
