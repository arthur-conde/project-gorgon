using System.Text.RegularExpressions;
using Gorgon.Shared.Logging;

namespace Samwise.Parsing;

/// <summary>
/// Parses Project Gorgon Player.log lines into GardenEvents.
/// Patterns mirror GorgonHelper.html (lines 2683, 2691, 2727, 2788, 2842, 2867, 2885, 2890, 2861).
/// </summary>
public sealed partial class GardenLogParser : ILogParser
{
    // Active-character tracking lives in ActiveCharacterLogSynchronizer.
    [GeneratedRegex(@"LocalPlayer: ProcessSetPetOwner\((\d+),", RegexOptions.CultureInvariant)]
    private static partial Regex SetPetOwnerRx();

    [GeneratedRegex(@"Download appearance loop @(\w+)\(scale=([\d.]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex AppearanceRx();

    [GeneratedRegex(@"ProcessUpdateDescription\((\d+),\s*""([^""]+)"",\s*""([^""]+)"",\s*""([^""]+)"",\s*\w+,\s*""\w+\(Scale=([\d.]+)\)"",\s*\d+\)", RegexOptions.CultureInvariant)]
    private static partial Regex UpdateDescRx();

    [GeneratedRegex(@"ProcessStartInteraction\((\d+),\s*\d+,\s*[\d.-]+,\s*\w+,\s*""(Summoned\w+)""\)", RegexOptions.CultureInvariant)]
    private static partial Regex StartInteractRx();

    // Real log shape: ProcessAddItem(BarleySeeds(86940428), -1, False)
    // Captures: itemName, itemId
    [GeneratedRegex(@"ProcessAddItem\((\w+)\((\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex AddItemRx();

    [GeneratedRegex(@"ProcessUpdateItemCode\((\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex UpdateItemCodeRx();

    // Fires instead of UpdateItemCode when a stack goes to zero (last seed used).
    [GeneratedRegex(@"ProcessDeleteItem\((\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex DeleteItemRx();

    [GeneratedRegex(@"ProcessUpdateSkill.*type=Gardening", RegexOptions.CultureInvariant)]
    private static partial Regex GardeningXpRx();

    [GeneratedRegex(@"ProcessScreenText.*ErrorMessage", RegexOptions.CultureInvariant)]
    private static partial Regex ScreenTextErrorRx();

    // Real log shape: ProcessErrorMessage(ItemUnusable, "Barley Seeds can't be used: You already have the maximum of that type of plant growing")
    // Captures the seed display name (group 1).
    [GeneratedRegex(@"ProcessErrorMessage\(ItemUnusable,\s*""([^""]+?) can't be used: You already have the maximum of that type of plant growing""", RegexOptions.CultureInvariant)]
    private static partial Regex PlantingCapRx();

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(line)) return null;

        var m = SetPetOwnerRx().Match(line);
        if (m.Success) return new SetPetOwner(timestamp, m.Groups[1].Value);

        m = AppearanceRx().Match(line);
        if (m.Success)
        {
            _ = double.TryParse(m.Groups[2].ValueSpan, System.Globalization.CultureInfo.InvariantCulture, out var scale);
            return new AppearanceLoop(timestamp, m.Groups[1].Value, scale);
        }

        m = UpdateDescRx().Match(line);
        if (m.Success)
        {
            _ = double.TryParse(m.Groups[5].ValueSpan, System.Globalization.CultureInfo.InvariantCulture, out var scale);
            return new UpdateDescription(timestamp, m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value, scale);
        }

        m = StartInteractRx().Match(line);
        if (m.Success) return new StartInteraction(timestamp, m.Groups[1].Value, m.Groups[2].Value);

        m = AddItemRx().Match(line);
        if (m.Success) return new AddItem(timestamp, m.Groups[2].Value, m.Groups[1].Value);

        m = UpdateItemCodeRx().Match(line);
        if (m.Success) return new UpdateItemCode(timestamp, m.Groups[1].Value);

        m = DeleteItemRx().Match(line);
        if (m.Success) return new DeleteItem(timestamp, m.Groups[1].Value);

        if (GardeningXpRx().IsMatch(line)) return new GardeningXp(timestamp);

        m = PlantingCapRx().Match(line);
        if (m.Success) return new PlantingCapReached(timestamp, m.Groups[1].Value);

        if (ScreenTextErrorRx().IsMatch(line)) return new ScreenTextError(timestamp);

        return null;
    }
}
