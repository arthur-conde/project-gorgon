using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// #931: icon templates are no longer shipped as embedded PG art — the
/// asset-extractor sidecar writes the manifest+blob cache to a directory at
/// runtime, and <see cref="BundledIconTemplateLoader.LoadFromDirectory"/> reads
/// it BCL-only. These tests build the cache fixture in-test with BCL primitives
/// only (no Tools.Common / AssetsTools / System.Drawing — that would re-leak the
/// decoders into the Mithril.slnx restore, the whole point of #921).
/// </summary>
public sealed class BundledIconTemplateLoaderTests : IDisposable
{
    private readonly string _dir;

    public BundledIconTemplateLoaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mithril931-icons-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed record Entry(string Name, string LandmarkType, double PivotX, double PivotY, int Width, int Height);

    // Builds icon-templates.{json,bin} in _dir with a matching SHA-256, BCL-only.
    private void WriteCacheFixture(IReadOnlyList<Entry> entries, bool corruptHash = false)
    {
        using var pixelMs = new MemoryStream();
        foreach (var e in entries)
        {
            int count = e.Width * e.Height;
            // gray then alpha; deterministic non-constant content.
            for (int i = 0; i < count; i++) pixelMs.WriteByte((byte)((i * 7 + e.Width) % 256));
            for (int i = 0; i < count; i++) pixelMs.WriteByte((byte)((i * 3 + e.Height) % 256));
        }
        var pixels = pixelMs.ToArray();
        var sha = Convert.ToHexStringLower(SHA256.HashData(pixels));
        if (corruptHash) sha = new string('0', sha.Length);

        var manifest = new
        {
            schemaVersion = 1,
            pixelSha256 = sha,
            icons = entries.Select(e => new
            {
                name = e.Name,
                landmarkType = e.LandmarkType,
                pivotX = e.PivotX,
                pivotY = e.PivotY,
                width = e.Width,
                height = e.Height,
            }).ToArray(),
        };
        File.WriteAllText(Path.Combine(_dir, "icon-templates.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        using var fs = File.Create(Path.Combine(_dir, "icon-templates.bin"));
        using var deflate = new DeflateStream(fs, CompressionLevel.Optimal);
        deflate.Write(pixels, 0, pixels.Length);
    }

    private static readonly Entry[] FourLandmarks =
    [
        new("landmark_telepad", "TeleportationPlatform", 0.5, 0.5, 8, 6),
        new("landmark_medipillar", "MeditationPillar", 0.5, 0.5, 6, 10),
        new("landmark_portal", "Portal", 0.5, 0.5, 7, 9),
        new("landmark_npc", "Npc", 0.5, 0.5, 5, 5),
    ];

    [Fact]
    public void Loads_all_four_landmark_types_from_cache_directory()
    {
        WriteCacheFixture(FourLandmarks);

        var set = BundledIconTemplateLoader.LoadFromDirectory(_dir, logger: null);

        set.Templates.Should().NotBeEmpty();
        var types = set.Templates.Select(t => t.LandmarkType).Distinct().ToList();
        types.Should().Contain(new[] { "TeleportationPlatform", "MeditationPillar", "Portal", "Npc" });
    }

    [Fact]
    public void Every_template_has_positive_dims_and_matching_buffer_lengths()
    {
        WriteCacheFixture(FourLandmarks);

        var set = BundledIconTemplateLoader.LoadFromDirectory(_dir, logger: null);

        foreach (var t in set.Templates)
        {
            t.Gray.Width.Should().BeGreaterThan(0);
            t.Gray.Height.Should().BeGreaterThan(0);
            t.Gray.Pixels.Length.Should().Be(t.Gray.Width * t.Gray.Height);
            t.Alpha.Width.Should().Be(t.Gray.Width);
            t.Alpha.Height.Should().Be(t.Gray.Height);
            t.Alpha.Pixels.Length.Should().Be(t.Gray.Width * t.Gray.Height);
        }
    }

    [Fact]
    public void Missing_cache_directory_yields_empty_set_safe_degrade()
    {
        var set = BundledIconTemplateLoader.LoadFromDirectory(
            Path.Combine(_dir, "does-not-exist"), logger: null);

        set.Templates.Should().BeEmpty();
    }

    [Fact]
    public void Hash_mismatch_yields_empty_set_safe_degrade()
    {
        WriteCacheFixture(FourLandmarks, corruptHash: true);

        var set = BundledIconTemplateLoader.LoadFromDirectory(_dir, logger: null);

        // Manifest hash doesn't match the blob → disabled, never a wrong load.
        set.Templates.Should().BeEmpty();
    }

    [Fact]
    public void ManifestPixelSha256_returns_recorded_hash()
    {
        WriteCacheFixture(FourLandmarks);

        var sha = BundledIconTemplateLoader.ManifestPixelSha256(_dir, logger: null);

        sha.Should().NotBeNullOrEmpty();
        sha!.Length.Should().Be(64); // SHA-256 lowercase hex
    }
}
