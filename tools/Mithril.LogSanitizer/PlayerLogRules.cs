using System.Text.RegularExpressions;

namespace Mithril.Tools.LogSanitizer;

public sealed partial class PlayerLogRules : ILogSourceRules
{
    // Banner: "[ts] Logged in as character <Name>. Time UTC=..." — \w+ stops at the trailing period.
    [GeneratedRegex(@"Logged in as character (?<name>\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex BannerPattern();

    // Real format: "LocalPlayer: ProcessAddPlayer(<int>, <int>, ""@appearance"", ""Name"", ""Description"", ...)".
    // Player name is the 4th argument — second quoted string. First quoted string is the "@"-prefixed appearance.
    // Skip past leading args + first quoted string, then capture the second quoted string.
    // Exclude `<` from the capture class so already-sanitized "<PLAYER_N>" names don't get re-discovered.
    [GeneratedRegex(@"LocalPlayer: ProcessAddPlayer\([^""]*""[^""]*"",\s*""(?<name>[^""<]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex AddPlayerPattern();

    public void DiscoverNames(string line, NameRegistry registry)
    {
        var bannerMatch = BannerPattern().Match(line);
        if (bannerMatch.Success)
        {
            registry.RegisterOwnCharacter(bannerMatch.Groups["name"].Value.Trim());
            return;
        }

        var addPlayerMatch = AddPlayerPattern().Match(line);
        if (addPlayerMatch.Success)
        {
            registry.RegisterOtherPlayer(addPlayerMatch.Groups["name"].Value.Trim());
        }
    }
}
