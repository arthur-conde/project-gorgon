using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Internal;

namespace Mithril.MapCalibration.Detection.Internal;

/// <summary>
/// Loads the bundled, pre-decoded icon templates from two embedded resources:
/// <c>BundledData/icon-templates.json</c> (a diffable schema-versioned metadata
/// manifest) + <c>BundledData/icon-templates.bin</c> (a DeflateStream-compressed
/// gray+alpha pixel payload). BCL-only — no System.Drawing, no WPF, no
/// AssetsTools.NET — so the in-process detection core ships without any image
/// decoder.
///
/// <para><b>Fail-soft:</b> a missing resource OR a <c>pixelSha256</c> mismatch
/// warns and returns <see cref="IconTemplateSet.Empty"/>. Empty templates →
/// no detections → the confidence gate rejects → no calibration persisted →
/// the system safe-degrades to manual/baseline, never a silent wrong match
/// (the "verify your inputs" guard from the gate study, applied to the shipped
/// artifact).</para>
/// </summary>
internal static class BundledIconTemplateLoader
{
    private const string ManifestResource = "Mithril.MapCalibration.BundledData.icon-templates.json";
    private const string BlobResource = "Mithril.MapCalibration.BundledData.icon-templates.bin";

    /// <summary>
    /// Loads + verifies the bundled template set. Returns
    /// <see cref="IconTemplateSet.Empty"/> on any failure (missing resource,
    /// parse error, hash mismatch, truncation).
    /// </summary>
    public static IconTemplateSet Load(ILogger? logger)
    {
        var assembly = typeof(BundledIconTemplateLoader).Assembly;

        var manifest = ReadManifest(assembly, logger);
        if (manifest is null) return IconTemplateSet.Empty;

        var pixels = ReadDecompressedPixels(assembly, logger);
        if (pixels is null) return IconTemplateSet.Empty;

        // Integrity gate: SHA-256 of the decompressed pixel stream must match the
        // manifest's recorded hash. Guards against manifest↔blob skew / truncation.
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(pixels));
        if (!string.Equals(actualHash, manifest.PixelSha256, StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogWarning(
                "Bundled icon-template pixel hash mismatch (manifest {Expected}, blob {Actual}) — icon templates disabled (safe-degrade).",
                manifest.PixelSha256, actualHash);
            return IconTemplateSet.Empty;
        }

        var templates = new List<IconTemplate>(manifest.Icons.Count);
        int offset = 0;
        foreach (var entry in manifest.Icons)
        {
            int count = entry.Width * entry.Height;
            if (count <= 0 || offset + 2 * count > pixels.Length)
            {
                logger?.LogWarning(
                    "Bundled icon-template blob too short for entry {Name} ({W}x{H}) — icon templates disabled (safe-degrade).",
                    entry.Name, entry.Width, entry.Height);
                return IconTemplateSet.Empty;
            }

            var gray = new byte[count];
            Array.Copy(pixels, offset, gray, 0, count);
            offset += count;
            var alpha = new byte[count];
            Array.Copy(pixels, offset, alpha, 0, count);
            offset += count;

            templates.Add(new IconTemplate(
                entry.Name,
                entry.LandmarkType,
                entry.PivotX,
                entry.PivotY,
                new GrayImage(entry.Width, entry.Height, gray),
                new GrayImage(entry.Width, entry.Height, alpha)));
        }

        logger?.LogInformation("Loaded {Count} bundled icon templates (pixelSha256 verified).", templates.Count);
        return new IconTemplateSet(templates);
    }

    /// <summary>
    /// Whether the committed manifest's <c>pixelSha256</c> matches the SHA-256 of
    /// the decompressed blob. The CI hard gate (the runtime <see cref="Load"/>
    /// degrades fail-soft; this surfaces a regen mistake as a red test).
    /// </summary>
    public static bool PixelSha256Verified(ILogger? logger)
    {
        var assembly = typeof(BundledIconTemplateLoader).Assembly;
        var manifest = ReadManifest(assembly, logger);
        var pixels = ReadDecompressedPixels(assembly, logger);
        if (manifest is null || pixels is null) return false;
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(pixels));
        return string.Equals(actualHash, manifest.PixelSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static IconTemplateManifest? ReadManifest(Assembly assembly, ILogger? logger)
    {
        using var stream = assembly.GetManifestResourceStream(ManifestResource);
        if (stream is null)
        {
            logger?.LogWarning("Bundled icon-template manifest {Resource} not found — icon templates disabled (safe-degrade).", ManifestResource);
            return null;
        }
        try
        {
            var manifest = JsonSerializer.Deserialize(stream, MapCalibrationJsonContext.Default.IconTemplateManifest);
            if (manifest is null || manifest.Icons is null || manifest.Icons.Count == 0 || string.IsNullOrEmpty(manifest.PixelSha256))
            {
                logger?.LogWarning("Bundled icon-template manifest empty/malformed — icon templates disabled (safe-degrade).");
                return null;
            }
            return manifest;
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Bundled icon-template manifest {Resource} failed to parse — icon templates disabled (safe-degrade).", ManifestResource);
            return null;
        }
    }

    private static byte[]? ReadDecompressedPixels(Assembly assembly, ILogger? logger)
    {
        using var stream = assembly.GetManifestResourceStream(BlobResource);
        if (stream is null)
        {
            logger?.LogWarning("Bundled icon-template blob {Resource} not found — icon templates disabled (safe-degrade).", BlobResource);
            return null;
        }
        try
        {
            using var deflate = new DeflateStream(stream, CompressionMode.Decompress);
            using var ms = new MemoryStream();
            deflate.CopyTo(ms);
            return ms.ToArray();
        }
        catch (InvalidDataException ex)
        {
            logger?.LogWarning(ex, "Bundled icon-template blob {Resource} failed to decompress — icon templates disabled (safe-degrade).", BlobResource);
            return null;
        }
    }
}
