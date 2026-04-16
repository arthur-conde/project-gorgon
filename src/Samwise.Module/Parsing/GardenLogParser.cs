using System.Text.RegularExpressions;
using Gorgon.Shared.Logging;

namespace Samwise.Parsing;

/// <summary>
/// Parses Project Gorgon Player.log lines into GardenEvents.
/// Patterns mirror GorgonHelper.html (lines 2683, 2691, 2727, 2788, 2842, 2867, 2885, 2890, 2861).
/// </summary>
public sealed partial class GardenLogParser : ILogParser
{
    // ProcessAddPlayer(entityId, uid, "PlayerModelDescriptor", "CharacterName", ...) — char name is the 2nd quoted arg.
    // Only match when prefixed with LocalPlayer: so we don't latch onto remote players.
    [GeneratedRegex(@"LocalPlayer:\s*ProcessAddPlayer\([^,]+,\s*[^,]+,\s*""[^""]*"",\s*""([^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex AddPlayerRx();

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

    [GeneratedRegex(@"ProcessUpdateSkill.*type=Gardening", RegexOptions.CultureInvariant)]
    private static partial Regex GardeningXpRx();

    [GeneratedRegex(@"ProcessScreenText.*ErrorMessage", RegexOptions.CultureInvariant)]
    private static partial Regex ScreenTextErrorRx();

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(line)) return null;

        var m = AddPlayerRx().Match(line);
        if (m.Success) return new PlayerLogin(timestamp, m.Groups[1].Value);

        m = SetPetOwnerRx().Match(line);
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

        if (GardeningXpRx().IsMatch(line)) return new GardeningXp(timestamp);
        if (ScreenTextErrorRx().IsMatch(line)) return new ScreenTextError(timestamp);

        return null;
    }
}
