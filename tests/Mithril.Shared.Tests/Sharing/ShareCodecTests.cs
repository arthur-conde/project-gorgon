using FluentAssertions;
using Mithril.Shared.Sharing;
using Xunit;

namespace Mithril.Shared.Tests.Sharing;

public class ShareCodecTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("hello")]
    [InlineData("the quick brown fox jumps over the lazy dog")]
    [InlineData("{\"schemaVersion\":2,\"eatenFoodsByInternalName\":{\"FoodBacon\":3}}")]
    public void EncodeDecode_round_trips(string text)
    {
        var encoded = ShareCodec.EncodePayload(text);
        var ok = ShareCodec.TryDecodePayload(encoded, out var decoded, out var error);

        ok.Should().BeTrue();
        error.Should().BeNull();
        decoded.Should().Be(text);
    }

    [Fact]
    public void Encoded_output_is_url_safe_base64url()
    {
        var encoded = ShareCodec.EncodePayload("the quick brown fox jumps over the lazy dog");

        encoded.Should().NotContain("+");
        encoded.Should().NotContain("/");
        encoded.Should().NotContain("=");
        encoded.Should().MatchRegex("^[A-Za-z0-9_-]+$");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_input_is_rejected(string? input)
    {
        var ok = ShareCodec.TryDecodePayload(input, out _, out var error);
        ok.Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Garbage_base64url_is_rejected()
    {
        // Length 1 is unrecoverable in base64url — should surface a format error rather
        // than throw.
        var ok = ShareCodec.TryDecodePayload("a", out _, out var error);
        ok.Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Wrong_version_byte_is_rejected()
    {
        // Build a version-99 envelope manually (still valid base64url, still gzip data
        // after the prefix) and confirm the codec refuses it instead of attempting to
        // decompress garbage.
        using var ms = new System.IO.MemoryStream();
        ms.WriteByte(99);
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("hello");
            gz.Write(bytes, 0, bytes.Length);
        }
        var b64url = Convert.ToBase64String(ms.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var ok = ShareCodec.TryDecodePayload(b64url, out _, out var error);
        ok.Should().BeFalse();
        error.Should().Contain("99");
    }

    [Fact]
    public void Decompressed_payload_above_safety_cap_is_rejected()
    {
        // Encode 257 KB of repeating 'a' — gzip compresses heavily so the encoded URL is
        // tiny, but decompression must trip the 256 KB safety cap.
        var huge = new string('a', ShareCodec.MaxDecompressedBytes + 1024);
        var encoded = ShareCodec.EncodePayload(huge);

        var ok = ShareCodec.TryDecodePayload(encoded, out _, out var error);
        ok.Should().BeFalse();
        error.Should().Contain("256 KB");
    }

    [Fact]
    public void Realistic_completionist_payload_fits_url_budget()
    {
        // 614 distinct foods is the rough Project Gorgon completionist count; force the
        // upper bound on internal-name length so the test is honest about worst case.
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"schemaVersion\":2,\"eatenFoodsByInternalName\":{");
        for (var i = 0; i < 614; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append("FoodOverlyLongInternalNameForStressTesting").Append(i.ToString("D3")).Append("\":99");
        }
        sb.Append("},\"lastReportTime\":\"2026-04-01T12:00:00+00:00\"}");

        var encoded = ShareCodec.EncodePayload(sb.ToString());
        encoded.Length.Should().BeLessThan(16_384, "fits inside the deep-link router's pippin payload cap");
    }
}
