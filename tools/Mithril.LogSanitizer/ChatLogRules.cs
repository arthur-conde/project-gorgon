using System.Text.RegularExpressions;

namespace Mithril.Tools.LogSanitizer;

public sealed partial class ChatLogRules : ILogSourceRules
{
    // Chat-side banner: "yy-MM-dd HH:MM:SS\t**** Logged In As <Name>. Server <X>. ..."
    // (Title-Case "In As" — distinct from Player.log's lowercase "in as character".)
    [GeneratedRegex(@"Logged In As (?<name>\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex ChatBannerPattern();

    // Chat lines: "yy-MM-dd HH:MM:SS\t[Channel] Speaker: msg".
    // Shape matches saruman.WordOfPowerChatParser.ChatLineRx in log-patterns.json.
    // "****"-prefixed system lines (area announcements) don't match the [Channel] bracket and are silently dropped.
    [GeneratedRegex(@"^\d{2}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\t\[(?<channel>[^\]]+)\] (?<name>[^:]+):", RegexOptions.CultureInvariant)]
    private static partial Regex ChatLinePattern();

    public void DiscoverNames(string line, NameRegistry registry)
    {
        var bannerMatch = ChatBannerPattern().Match(line);
        if (bannerMatch.Success)
        {
            registry.RegisterOwnCharacter(bannerMatch.Groups["name"].Value.Trim());
            return;
        }

        var chatMatch = ChatLinePattern().Match(line);
        if (chatMatch.Success)
        {
            registry.RegisterOtherPlayer(chatMatch.Groups["name"].Value.Trim());
        }
    }
}
