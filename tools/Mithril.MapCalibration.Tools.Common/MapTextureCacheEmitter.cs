using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mithril.MapCalibration.Detection;

namespace Mithril.Tools.MapCalibration.Common;

/// <summary>
/// Writes the gray-only base-texture cache the runtime
/// <c>CachedBaseTextureProvider</c> consumes (issue #931): a schema-versioned
/// metadata manifest (<c>map-texture-&lt;area&gt;.json</c>) + a DeflateStream-
/// compressed single-channel gray pixel blob (<c>map-texture-&lt;area&gt;.bin</c>).
/// Mirrors <see cref="IconTemplateEmitter"/>'s deflate+SHA pattern but gray-only
/// (no alpha) — the base texture is a single channel the detector diffs the
/// screenshot against.
///
/// <para>Decoder-side: the input PNG is read via <see cref="ImageIo.LoadGray"/>
/// (System.Drawing), so this lives in tools/ alongside the extractors, off the
/// shipped src/** graph. <see cref="PixelSha256"/> is over the decompressed gray
/// stream — the same integrity contract the loader re-verifies, and the value the
/// canonical-hash gate compares against.</para>
/// </summary>
public static class MapTextureCacheEmitter
{
    private const int SchemaVersion = 1;

    private sealed record Manifest(
        int SchemaVersion,
        string Area,
        int Width,
        int Height,
        string PixelSha256,
        string? PgVersion,
        string? ExtractorVersion);

    /// <summary>
    /// Converts the extracted base-texture <paramref name="texturePngPath"/> to
    /// the gray-only deflate cache format under <paramref name="outDir"/>. Returns
    /// the written manifest path + the pixelSha256.
    /// </summary>
    public static (string ManifestPath, string PixelSha256) EmitFromPng(
        string texturePngPath,
        string area,
        string outDir,
        string? pgVersion,
        string? extractorVersion)
    {
        Directory.CreateDirectory(outDir);

        var gray = ImageIo.LoadGray(texturePngPath);
        var pixels = gray.Pixels;
        var sha = Convert.ToHexStringLower(SHA256.HashData(pixels));

        var manifest = new Manifest(SchemaVersion, area, gray.Width, gray.Height, sha, pgVersion, extractorVersion);
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        var manifestPath = Path.Combine(outDir, $"map-texture-{area}.json");
        File.WriteAllText(manifestPath, json + "\n", new UTF8Encoding(false));

        var binPath = Path.Combine(outDir, $"map-texture-{area}.bin");
        using (var fs = File.Create(binPath))
        using (var deflate = new DeflateStream(fs, CompressionLevel.Optimal))
        {
            deflate.Write(pixels, 0, pixels.Length);
        }

        Console.WriteLine($"[emit-texture] {area} {gray.Width}x{gray.Height} -> {outDir}");
        Console.WriteLine($"[emit-texture] pixelSha256 = {sha}");
        Console.WriteLine($"[emit-texture] map-texture-{area}.bin = {new FileInfo(binPath).Length} bytes (deflated)");
        return (manifestPath, sha);
    }
}
