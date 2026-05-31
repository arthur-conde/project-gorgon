using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection;
using Mithril.Reference.Models.Misc;
using Mithril.Shared.Reference;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Builds <see cref="LandmarkReference"/>s for an area from
/// <see cref="IReferenceDataService"/>: landmarks (<c>landmarks.json</c>) keyed by
/// area + NPCs (<c>npcs.json</c>, the full POCO that carries <see cref="Npc.Pos"/>)
/// whose <see cref="Npc.AreaName"/> matches the area.
///
/// <para>Each source is mapped to a <see cref="LandmarkReference"/> whose
/// <c>Type</c> is one of the four template/solver tokens
/// (<c>landmark_telepad</c> / <c>landmark_medipillar</c> / <c>landmark_portal</c>
/// / <c>landmark_npc</c>) the detector pairs against. World coords are parsed
/// from the <c>"x:N y:N z:N"</c> position string (the live <c>landmarks.json</c> /
/// <c>npcs.json</c> shape — memory <c>pg_reference_coords_are_world_frame</c>).</para>
/// </summary>
public sealed class ReferenceDataAreaReferenceProvider : IAreaReferenceProvider
{
    // Verification owed (#914): confirm the Landmark.Type → template-token mapping
    // against live landmarks.json. Confirmed against v470 corpus on 2026-05-31:
    // Type ∈ { Portal, MeditationPillar, TeleportationPlatform } — every landmark
    // carries exactly one of these. The four template tokens come from
    // IconTemplateExtractor's canonical pairing (tools/Mithril.MapCalibration.Tools.Common):
    //   Portal → landmark_portal, MeditationPillar → landmark_medipillar,
    //   TeleportationPlatform → landmark_telepad, (NPCs) → landmark_npc.
    // Any future/unmapped Type is WARNED and dropped (no silent coercion to a
    // wrong token, which would mispair a detection and corrupt the solve).
    private static readonly IReadOnlyDictionary<string, string> LandmarkTypeToToken =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Portal"] = "landmark_portal",
            ["MeditationPillar"] = "landmark_medipillar",
            ["TeleportationPlatform"] = "landmark_telepad",
        };

    private const string NpcToken = "landmark_npc";

    private readonly IReferenceDataService _refData;
    private readonly ILogger? _logger;

    public ReferenceDataAreaReferenceProvider(IReferenceDataService refData, ILogger? logger = null)
    {
        _refData = refData;
        _logger = logger;
    }

    public IReadOnlyList<LandmarkReference> ForArea(string areaKey)
    {
        if (string.IsNullOrWhiteSpace(areaKey)) return Array.Empty<LandmarkReference>();

        var result = new List<LandmarkReference>();
        // Count references dropped for unparseable coords (NOT the unmapped-type
        // case, which is warned per-entry above). Aggregated into ONE warning per
        // area so a future landmarks.json / npcs.json coord-shape change is visible
        // rather than silently shrinking the reference set (Fix E).
        var unparseableCoords = 0;

        if (_refData.Landmarks.TryGetValue(areaKey, out var landmarks))
        {
            foreach (var lm in landmarks)
            {
                if (lm is null || string.IsNullOrEmpty(lm.Type)) continue;
                if (!LandmarkTypeToToken.TryGetValue(lm.Type, out var token))
                {
                    // No silent drop: surface the unmapped type so an uncovered
                    // landmark category is visible (verification-owed follow-up).
                    _logger?.LogWarning(
                        "Unmapped landmark Type {Type} in area {Area} (landmark {Name}); dropped — no template token. " +
                        "Verification owed (#914): confirm Landmark.Type → template-token mapping vs live landmarks.json.",
                        lm.Type, areaKey, lm.Name);
                    continue;
                }
                if (TryParseWorld(lm.Loc, out var world))
                {
                    result.Add(new LandmarkReference(token, lm.Name ?? lm.Type, world));
                }
                else
                {
                    unparseableCoords++;
                }
            }
        }

        foreach (var npc in _refData.NpcsByInternalName.Values)
        {
            if (npc is null) continue;
            if (!string.Equals(npc.AreaName, areaKey, StringComparison.Ordinal)) continue;
            if (TryParseWorld(npc.Pos, out var world))
            {
                result.Add(new LandmarkReference(NpcToken, npc.Name ?? "NPC", world));
            }
            else
            {
                unparseableCoords++;
            }
        }

        if (unparseableCoords > 0)
        {
            _logger?.LogWarning(
                "Dropped {Count} reference(s) in area {Area} with unparseable coords — possible landmarks.json / npcs.json "
                + "coord-shape change. Verification owed (#914): confirm the \"x:N y:N z:N\" position shape vs live data.",
                unparseableCoords, areaKey);
        }

        return result;
    }

    /// <summary>
    /// Parses a PG position string. The live shape is <c>"x:N y:N z:N"</c>
    /// (landmarks.json / npcs.json, v470); also tolerates a bare <c>"x y z"</c>
    /// or <c>"x,y,z"</c> form so a future shape variant degrades to a skip, not a
    /// crash. Returns false (skip this reference) on any malformed input.
    /// </summary>
    internal static bool TryParseWorld(string? loc, out WorldCoord world)
    {
        world = default;
        if (string.IsNullOrWhiteSpace(loc)) return false;

        // Strip "x:" / "y:" / "z:" labels if present, then split on whitespace/comma.
        Span<double> coords = stackalloc double[3];
        var count = 0;
        foreach (var rawToken in loc.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = rawToken;
            var colon = token.IndexOf(':');
            if (colon >= 0) token = token[(colon + 1)..];
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return false; // a non-numeric token → malformed → skip
            if (count >= 3) return false; // more than 3 numbers → unexpected shape → skip
            coords[count++] = value;
        }

        if (count != 3) return false;
        world = new WorldCoord(coords[0], coords[1], coords[2]);
        return true;
    }
}
