using System.Text.RegularExpressions;
using Mithril.Shared.Logging;
using Saruman.Domain;
using Saruman.Services;

namespace Saruman.Parsing;

public sealed partial class WordOfPowerChatParser : IChatLogParser
{
    private readonly SarumanCodebookService _codebook;

    public WordOfPowerChatParser(SarumanCodebookService codebook)
    {
        _codebook = codebook;
    }

    // Format: "YY-MM-DD HH:MM:SS\t[Channel] Speaker: message".
    // We only need the speaker for attribution; the Saruman module doesn't use
    // it today but recording it keeps the event model complete.
    [GeneratedRegex(
        @"^\d{2}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\s*\t\s*\[[^\]]+\]\s*(?<speaker>[^:]+?)\s*:\s*(?<msg>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ChatLineRx();

    // Words of Power are runs of uppercase letters. Real codes seen: 6–11 chars.
    // We still scan every uppercase run and validate against the codebook, so
    // shouts like HOOOWL or MUAHAHAH never match unless the player has
    // discovered a WoP with that exact spelling.
    [GeneratedRegex(@"\b[A-Z]{4,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex UpperTokenRx();

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var m = ChatLineRx().Match(line);
        string speaker;
        string msg;
        if (m.Success)
        {
            speaker = m.Groups["speaker"].Value;
            msg = m.Groups["msg"].Value;
        }
        else
        {
            // Fall back to scanning the whole line if format is unexpected.
            speaker = string.Empty;
            msg = line;
        }

        foreach (Match tok in UpperTokenRx().Matches(msg))
        {
            var code = tok.Value;
            if (_codebook.IsTracked(code))
                return new WordOfPowerSpoken(timestamp, speaker, code);
        }
        return null;
    }
}
