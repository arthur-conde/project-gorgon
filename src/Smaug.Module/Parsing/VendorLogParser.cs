using System.Text.RegularExpressions;

namespace Smaug.Parsing;

/// <summary>
/// Parses Player.log lines into vendor-related <see cref="VendorEvent"/> records.
/// Patterns verified against the real log:
///   ProcessVendorScreen(entityId, FavorTier, gold, reset, cap, "desc", VendorInfo[], ...)
///   ProcessVendorAddItem(price, ItemName(instanceId), bool)
///   ProcessVendorUpdateAvailableGold(gold, reset, cap)
///   ProcessStartInteraction(entityId, uid, favor, bool, "NPC_Key")
///
/// <para>Civic Pride is intentionally <b>not</b> parsed here — it lives in the
/// shared <see cref="Mithril.GameState.Skills.IPlayerSkillState"/> service
/// (which already tails <c>ProcessLoadSkills</c> / <c>ProcessUpdateSkill</c>),
/// and <c>VendorIngestionService</c> consumes the projected
/// <c>PlayerSkillSnapshot</c> directly. Per <see href="https://github.com/moumantai-gg/mithril/issues/580">#580</see>
/// (Class A finding #2 from the post-#578 cross-cutting consumer audit) modules
/// don't re-parse skill verbs.</para>
///
/// <para>Active-character tracking lives in <c>ActiveCharacterLogSynchronizer</c> —
/// this parser does not handle <c>ProcessAddPlayer</c>.</para>
/// </summary>
public sealed partial class VendorLogParser
{
    [GeneratedRegex(@"ProcessVendorScreen\((-?\d+),\s*(\w+),\s*(-?\d+),\s*(\d+),\s*(\d+),",
        RegexOptions.CultureInvariant)]
    private static partial Regex VendorScreenRx();

    [GeneratedRegex(@"ProcessVendorAddItem\((\d+),\s*(\w+)\((\d+)\),",
        RegexOptions.CultureInvariant)]
    private static partial Regex VendorAddItemRx();

    [GeneratedRegex(@"ProcessVendorUpdateAvailableGold\((\d+),\s*(\d+),\s*(\d+)\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex VendorGoldRx();

    [GeneratedRegex(@"ProcessStartInteraction\((\d+),\s*\d+,\s*[\d.-]+,\s*\w+,\s*""(NPC_\w+)""\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex StartInteractionRx();

    public VendorEvent? TryParse(string line, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(line)) return null;

        // VendorScreen is less frequent than AddItem, but a single ProcessLoadSkills line
        // can contain hundreds of thousands of characters of skill JSON, so short-circuit
        // on substring matches before running regex.
        if (line.Contains("ProcessVendorAddItem", StringComparison.Ordinal))
        {
            var m = VendorAddItemRx().Match(line);
            if (m.Success
                && long.TryParse(m.Groups[1].ValueSpan, out var price)
                && long.TryParse(m.Groups[3].ValueSpan, out var instanceId))
                return new VendorItemSold(timestamp, price, m.Groups[2].Value, instanceId);
        }

        if (line.Contains("ProcessVendorScreen", StringComparison.Ordinal))
        {
            var m = VendorScreenRx().Match(line);
            if (m.Success
                && int.TryParse(m.Groups[1].ValueSpan, out var entityId)
                && long.TryParse(m.Groups[3].ValueSpan, out var gold)
                && long.TryParse(m.Groups[5].ValueSpan, out var cap))
                return new VendorScreenOpened(timestamp, entityId, m.Groups[2].Value, gold, cap);
        }

        if (line.Contains("ProcessVendorUpdateAvailableGold", StringComparison.Ordinal))
        {
            var m = VendorGoldRx().Match(line);
            if (m.Success
                && long.TryParse(m.Groups[1].ValueSpan, out var gold)
                && long.TryParse(m.Groups[3].ValueSpan, out var cap))
                return new VendorGoldUpdated(timestamp, gold, cap);
        }

        if (line.Contains("ProcessStartInteraction", StringComparison.Ordinal))
        {
            var m = StartInteractionRx().Match(line);
            if (m.Success && int.TryParse(m.Groups[1].ValueSpan, out var entityId))
                return new NpcInteractionStarted(timestamp, entityId, m.Groups[2].Value);
        }

        return null;
    }
}
