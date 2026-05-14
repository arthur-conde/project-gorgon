using System.Diagnostics.CodeAnalysis;

namespace Silmarillion.Navigation;

/// <summary>
/// Translates a recipe slot's <c>ItemKeys</c> list into an Items-tab query fragment, when
/// every key can be expressed against the existing <c>Item</c> query schema. All-or-nothing:
/// if any key is unmappable, the whole slot fails so the chip stays non-navigable rather
/// than emit a lossy filter.
/// </summary>
/// <remarks>
/// v1 mapping rules:
/// <list type="bullet">
///   <item>Bare tag (no <c>:</c>) → <c>Keywords CONTAINS "tag"</c>.</item>
///   <item><c>EquipmentSlot:X</c> → <c>EquipSlot = "X"</c> (lossless; <c>Item.EquipSlot</c> is queryable).</item>
///   <item>Anything else with a <c>:</c> prefix (<c>MinTSysPrereq:N</c>, <c>MaxTSysPrereq:N</c>,
///         <c>SkillPrereq:X</c>, <c>MinValue:N</c>, <c>MinRarity:X</c>) → fail. The synthesized
///         keyword index from <c>ItemKeywordSynthesis.Enrich</c> isn't exposed to the Items query
///         engine, and no direct <c>Item</c> property captures these prereq constraints today.</item>
/// </list>
/// </remarks>
public static class ItemKeywordQueryMapper
{
    public static bool TryBuildQuery(IReadOnlyList<string> itemKeys, [NotNullWhen(true)] out string? query)
    {
        if (itemKeys.Count == 0)
        {
            query = null;
            return false;
        }

        var fragments = new string[itemKeys.Count];
        for (var i = 0; i < itemKeys.Count; i++)
        {
            if (!TryMapOne(itemKeys[i], out var fragment))
            {
                query = null;
                return false;
            }
            fragments[i] = fragment;
        }

        query = string.Join(" AND ", fragments);
        return true;
    }

    private static bool TryMapOne(string key, [NotNullWhen(true)] out string? fragment)
    {
        var colon = key.IndexOf(':');
        if (colon < 0)
        {
            fragment = $"Keywords CONTAINS \"{key}\"";
            return true;
        }

        var prefix = key.AsSpan(0, colon);
        var value = key.AsSpan(colon + 1);
        if (prefix.SequenceEqual("EquipmentSlot"))
        {
            fragment = $"EquipSlot = \"{value}\"";
            return true;
        }

        fragment = null;
        return false;
    }
}
