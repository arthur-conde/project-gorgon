using System.Text.RegularExpressions;

namespace Mithril.Tools.LogSanitizer;

public sealed partial class PlayerLogRules : ILogSourceRules
{
    [GeneratedRegex(@"Logged in as character (?<name>\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex BannerPattern();

    [GeneratedRegex(@"LocalPlayer: ProcessAddPlayer\((?<name>[^,)]+)", RegexOptions.CultureInvariant)]
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
