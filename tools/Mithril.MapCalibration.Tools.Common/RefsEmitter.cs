using System.Text;
using System.Text.Json;

namespace Mithril.Tools.MapCalibration.Common;

/// <summary>
/// Writes the local-only replay reference fixtures
/// (<c>study/refs/&lt;area&gt;.json</c>) the
/// <c>ReplayFixtureTests</c> consumes: a self-contained
/// <c>[{type,name,x,z}, …]</c> list of an area's landmark + NPC world
/// references, sourced from the SAME bundled reference data the proven
/// gate-study tool used (<c>landmarks.json</c> + <c>npcs.json</c> via
/// <see cref="LandmarksReader"/> / <see cref="NpcsReader"/>).
///
/// <para>The emitted files live under the gitignored <c>study/</c> tree — they
/// are reproducible local fixtures, never committed. This generator is the
/// reproducible regen path so the refs can be rebuilt deterministically from
/// the bundled data (see the tool README's <c>emit-refs</c> section).</para>
///
/// <para>Refs carry the world frame the calibration solver pairs against: the
/// <c>Type</c> string ("TeleportationPlatform" / "MeditationPillar" / "Portal"
/// / "Npc") matches each icon template's <c>LandmarkType</c> for the
/// type-constrained RANSAC correspondence.</para>
/// </summary>
public static class RefsEmitter
{
    private sealed record RefEntry(string Type, string Name, double X, double Z);

    /// <summary>
    /// Emit <c>&lt;outDir&gt;/&lt;area&gt;.json</c> for the given area from the
    /// bundled landmarks + npcs reference data. Returns the count written.
    /// </summary>
    public static int Emit(string area, string landmarksJsonPath, string npcsJsonPath, string outDir)
    {
        var landmarks = LandmarksReader.LoadForArea(landmarksJsonPath, area);
        var npcs = NpcsReader.LoadForArea(npcsJsonPath, area);

        var entries = landmarks.Concat(npcs)
            .Select(r => new RefEntry(r.Type, r.Name, r.World.X, r.World.Z))
            .ToList();

        if (entries.Count == 0)
        {
            throw new UserFacingException(
                $"no landmark/NPC references found for area '{area}' in {Path.GetFileName(landmarksJsonPath)} / {Path.GetFileName(npcsJsonPath)}");
        }

        Directory.CreateDirectory(outDir);
        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        var outPath = Path.Combine(outDir, area + ".json");
        File.WriteAllText(outPath, json + "\n", new UTF8Encoding(false));

        Console.WriteLine($"[emit-refs] {area}: {landmarks.Count} landmarks + {npcs.Count} NPCs -> {outPath}");
        return entries.Count;
    }
}
