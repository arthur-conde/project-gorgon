using System.Collections.Generic;
using Mithril.MapCalibration;            // WorldCoord
using Mithril.MapCalibration.Detection;  // LandmarkReference

namespace Mithril.Tools.MapCalibration.Harness.Tests;

/// <summary>
/// The 38 AreaEltibule references the live <c>ReferenceDataAreaReferenceProvider</c>
/// emits (v470): 9 Portal + 6 MeditationPillar + 6 TeleportationPlatform + 17 Npc.
/// World coords are (X, Y, Z); the solver pairs on X/Z. The 2 positionless npcs.json
/// entries (Work Orders sign, Sacrificial Bowl pedestal) are intentionally absent.
///
/// <para>Shared regression fixture for <see cref="EccRegistrationRefinerTests"/>, the
/// #978 ECC-registration acceptance suite. (Extracted from the #938 investigation
/// repro when that exploratory scaffolding was pruned — findings live in mithril#979.)</para>
/// </summary>
internal static class EltibuleReferences
{
    public static List<LandmarkReference> All()
    {
        static LandmarkReference R(string type, string name, double x, double y, double z) =>
            new(type, name, new WorldCoord(x, y, z));

        return new List<LandmarkReference>
        {
            // Portals
            R("Portal", "Strange Gateway", 954.409973, 93.550003, 437.089996),
            R("Portal", "Lord Eltibule's Residence", 1138.161865, 39.093884, 1367.77417),
            R("Portal", "Hogan's Keep", 1495.542236, 113.689079, 336.063507),
            R("Portal", "Cellar Entrance", 1138.047729, 39.093884, 1349.43811),
            R("Portal", "Travel", 1167.875, 129.005005, 2924.259033),
            R("Portal", "Employees Only", 2438.050049, 112.720001, 2614.22998),
            R("Portal", "Boarded Up Entrance", 2108.312988, 40.327, 2104.249023),
            R("Portal", "Crypt Entrance", 1099.73999, 48.342999, 1505.755981),
            R("Portal", "Travel to Kur Mountains", 1517.650024, 114.507004, 272.23999),
            // Meditation pillars (__15 / __16 are co-located in the data)
            R("MeditationPillar", "Meditation Pillar", 2408.116455, 128.816833, 2021.867554),
            R("MeditationPillar", "Meditation Pillar", 1562.388428, 112.367661, 424.236877),
            R("MeditationPillar", "Meditation Pillar", 1562.388428, 112.367661, 424.236877),
            R("MeditationPillar", "Meditation Pillar", 1934.636841, 37.967136, 1308.01709),
            R("MeditationPillar", "Meditation Pillar", 941.062805, 27.910137, 1543.284912),
            R("MeditationPillar", "Meditation Pillar", 916.844788, 96.698303, 2428.760254),
            // Teleportation platforms
            R("TeleportationPlatform", "TeleportCircle_PlateauAlt", 2334.690186, 135.734726, 841.390625),
            R("TeleportationPlatform", "TeleportCircle_SieAntry", 1977.883789, 41.054588, 1373.350098),
            R("TeleportationPlatform", "TeleportCircle_Courtyard", 1114.727173, 39.016926, 1355.878052),
            R("TeleportationPlatform", "TeleportCircle_PlateauCity", 604.247681, 134.018173, 1494.122192),
            R("TeleportationPlatform", "TeleportCircle_AbandonedCourtyard", 1519.629761, 113.419998, 398.076965),
            R("TeleportationPlatform", "TeleportCircle_BFE1", 2550.061768, 1.430984, 1352.015747),
            // NPCs
            R("Npc", "Braigon", 1099.952026, 37.634277, 1398.786987),
            R("Npc", "George Madler", 1107.27002, 37.634277, 1397.030029),
            R("Npc", "Gretchen Salas", 1151.800049, 41.096237, 1277.800049),
            R("Npc", "Helena Veilmoor", 1584.709961, 111.967621, 473.345245),
            R("Npc", "Hogan", 1531.617432, 111.967621, 441.754822),
            R("Npc", "Jesina", 1939.183594, 39.621506, 1361.98999),
            R("Npc", "Jumjab", 1988.909424, 196.989639, 470.373993),
            R("Npc", "Kalaba", 1068.28894, 38.251373, 1335.883057),
            R("Npc", "Kleave", 1083.234985, 37.650242, 1332.546021),
            R("Npc", "Mythander", 1580.430054, 111.967621, 430.109985),
            R("Npc", "Oritania", 1121.492798, 37.634277, 1340.76416),
            R("Npc", "Percy Evans", 439.309998, 48.147507, 632.599976),
            R("Npc", "Sie Antry", 1944.559814, 39.621506, 1367.099365),
            R("Npc", "Suspicious Cow", 1181.699951, 31.682209, 1167.290039),
            R("Npc", "Thimble Pete", 1526.0, 111.959999, 458.029999),
            R("Npc", "Yasinda", 1580.857056, 111.958, 390.020996),
            R("Npc", "Yetta", 1089.330444, 37.641918, 1388.192139),
        };
    }
}
