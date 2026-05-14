using Mithril.Shared.Reference;

namespace Silmarillion.ViewModels;

/// <summary>
/// Resolves an NPC's friendly display name from its envelope-key InternalName, e.g.
/// <c>"NPC_Joeh"</c> → <c>"Joeh"</c>. Used wherever an NPC appears as a label —
/// the NPCs-tab master list, the <c>Vendor: NPC_X</c> / <c>Training: NPC_X</c> source
/// chips on item and recipe details. Mirrors the NpcEntry projection rule already used
/// by Arwen: prefer <see cref="Mithril.Reference.Models.Npcs.Npc.Name"/> when set,
/// otherwise strip the leading <c>NPC_</c> prefix so the envelope key still reads
/// reasonably even for entries that ship without a Name.
/// </summary>
internal static class NpcNameResolver
{
    public static string Resolve(IReferenceDataService refData, string internalName)
    {
        if (refData.NpcsByInternalName.TryGetValue(internalName, out var npc)
            && !string.IsNullOrEmpty(npc.Name))
        {
            return npc.Name!;
        }
        return StripNpcPrefix(internalName);
    }

    public static string StripNpcPrefix(string internalName) =>
        internalName.StartsWith("NPC_", StringComparison.Ordinal)
            ? internalName.Substring(4)
            : internalName;
}
