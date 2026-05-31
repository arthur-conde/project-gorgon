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
/// #949: the icon templates resolve via a per-attempt
/// <see cref="IIconTemplateProvider"/> (re-reads the cache each call) rather than an
/// eager singleton, so a same-session populate engages immediately — the headline
/// acceptance criterion (first-session calibration on a fresh icon cache, no
/// restart). These tests build the cache fixture in-test with BCL primitives only
/// (no AssetsTools / System.Drawing — that would re-leak the decoders, #921).
/// </summary>
public sealed class CachedIconTemplateProviderTests : IDisposable
{
    private readonly string _dir;

    public CachedIconTemplateProviderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mithril949-iconprovider-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Empty_cache_dir_yields_empty_templates_safe_degrade()
    {
        var provider = new CachedIconTemplateProvider(_dir, logger: null);

        provider.GetTemplates().Templates.Should().BeEmpty();
    }

    [Fact]
    public void Missing_cache_dir_yields_empty_templates_safe_degrade()
    {
        var provider = new CachedIconTemplateProvider(Path.Combine(_dir, "nope"), logger: null);

        provider.GetTemplates().Templates.Should().BeEmpty();
    }

    [Fact]
    public void Same_session_populate_is_picked_up_within_one_process_lifetime()
    {
        // THE headline acceptance criterion (#949): a provider that first reads an
        // empty cache must return the populated set on the NEXT call once the cache
        // is populated — no process restart.
        var provider = new CachedIconTemplateProvider(_dir, logger: null);

        provider.GetTemplates().Templates.Should().BeEmpty("cache is empty initially");

        WriteCacheFixture(FourLandmarks); // sidecar populates the cache, same session

        var after = provider.GetTemplates();
        after.Templates.Should().NotBeEmpty("the per-attempt provider re-reads the cache");
        after.Templates.Select(t => t.LandmarkType).Distinct()
            .Should().Contain(new[] { "TeleportationPlatform", "MeditationPillar", "Portal", "Npc" });
    }

    [Fact]
    public void Repeated_calls_over_a_stable_cache_return_the_same_memoised_set()
    {
        WriteCacheFixture(FourLandmarks);
        var provider = new CachedIconTemplateProvider(_dir, logger: null);

        var a = provider.GetTemplates();
        var b = provider.GetTemplates();

        a.Templates.Should().NotBeEmpty();
        b.Should().BeSameAs(a, "the loaded set is memoised keyed by the manifest hash (no re-decompress)");
    }

    [Fact]
    public void Hash_mismatch_yields_empty_templates_safe_degrade()
    {
        WriteCacheFixture(FourLandmarks, corruptHash: true);
        var provider = new CachedIconTemplateProvider(_dir, logger: null);

        provider.GetTemplates().Templates.Should().BeEmpty();
    }

    // ── Fixture (BCL-only, mirrors BundledIconTemplateLoaderTests) ────────────

    private sealed record Entry(string Name, string LandmarkType, double PivotX, double PivotY, int Width, int Height);

    private void WriteCacheFixture(IReadOnlyList<Entry> entries, bool corruptHash = false)
    {
        using var pixelMs = new MemoryStream();
        foreach (var e in entries)
        {
            int count = e.Width * e.Height;
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
}
