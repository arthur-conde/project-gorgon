using System.Text.Json;
using Mithril.MapCalibration;

namespace Mithril.Tools.MapCalibration.Common;

/// <summary>
/// Reads the subset of <c>npcs.json</c> we need: per-area list of NPCs with
/// a parseable <c>Pos</c> field. Shape is a top-level dict keyed by NPC
/// internal name; each value has <c>AreaName</c>, <c>Name</c>, and optionally
/// <c>Pos</c> (format "x:N y:N z:N", same frame as landmarks.json Loc).
///
/// <para>Many NPCs ship without <c>Pos</c> (e.g. unique-quest NPCs, chest
/// references, work-order signs) — those are silently skipped. The bundled
/// data has ~300 NPCs with positions; ~30-40 per populated area, which
/// gives the solver an order of magnitude more reference points than
/// landmarks alone.</para>
/// </summary>
public static class NpcsReader
{
    public static IReadOnlyList<LandmarkRef> LoadForArea(string npcsJsonPath, string area)
    {
        if (!File.Exists(npcsJsonPath))
        {
            throw new UserFacingException($"npcs.json not found: {npcsJsonPath}");
        }

        using var stream = File.OpenRead(npcsJsonPath);
        using var doc = JsonDocument.Parse(stream);

        var list = new List<LandmarkRef>();
        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            var v = entry.Value;
            if (v.ValueKind != JsonValueKind.Object) continue;
            if (!v.TryGetProperty("AreaName", out var areaProp)) continue;
            if (!string.Equals(areaProp.GetString(), area, StringComparison.Ordinal)) continue;
            if (!v.TryGetProperty("Pos", out var posProp)) continue;
            var posStr = posProp.GetString();
            var world = WorldCoord.TryParse(posStr);
            if (world is null) continue;
            var name = v.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() ?? entry.Name : entry.Name;
            list.Add(new LandmarkRef("Npc", name, world.Value));
        }
        return list;
    }
}
