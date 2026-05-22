using System.Globalization;
using System.Text.RegularExpressions;

namespace Mithril.WorldSim.Chat.Producers;

/// <summary>
/// Recognises the chat-side login banner that PG writes at the head of every
/// chat session: <c>**** Logged In As &lt;Character&gt;. Server &lt;Server&gt;.
/// Timezone Offset &lt;HH:MM:SS&gt;.</c> (the literal <c>****</c> star bar
/// varies in length, so the regex matches the textual fields only). Used by
/// <see cref="ChatLogProducer"/> to update <see cref="IChatSessionService"/>
/// as banners flow past, and by <see cref="ChatLogReplaySource"/> to find the
/// seek-point for session-replay (principle 9).
///
/// <para>The existing <c>Mithril.Shared.Logging.ChatLogClock</c> has a sibling
/// regex that captures the <c>Timezone Offset</c> only — its concern is
/// timestamp folding, not <c>(Server, Character)</c> identification, so it
/// intentionally doesn't capture those fields. This parser captures all three.</para>
/// </summary>
public static partial class ChatLoginBannerParser
{
    // Capture both the character name and the server name. The fields are
    // terminated by `.` per PG's banner format. `[^.]+` greedy is sufficient
    // because no PG name contains a literal period in either field. The
    // offset is signed `HH:MM:SS` (PG omits the `+` for east-of-UTC),
    // matching the LoginBannerParser shape on the Player-log side.
    [GeneratedRegex(
        @"Logged In As (?<character>[^.]+)\. Server (?<server>[^.]+)\. Timezone Offset (?<off>-?\d{1,2}:\d{2}:\d{2})",
        RegexOptions.CultureInvariant)]
    private static partial Regex BannerRx();

    /// <summary>
    /// Try to parse the chat-banner fields from a chat-log line. Returns
    /// <c>true</c> with <paramref name="banner"/> populated if the line is
    /// a banner; <c>false</c> otherwise.
    /// </summary>
    public static bool TryParse(string line, out ChatLoginBanner banner)
    {
        banner = default;
        if (line is null) return false;

        // Cheap negative-match short-circuit: the literal substring is
        // present in every banner and absent from every non-banner chat
        // line. Skips the regex entirely on the 99%+ common case (every
        // non-banner line in every channel file).
        if (line.IndexOf("Logged In As ", StringComparison.Ordinal) < 0) return false;

        var m = BannerRx().Match(line);
        if (!m.Success) return false;

        if (!TimeSpan.TryParse(m.Groups["off"].Value, CultureInfo.InvariantCulture, out var offset))
            return false;

        banner = new ChatLoginBanner(
            Server: m.Groups["server"].Value,
            Character: m.Groups["character"].Value,
            Offset: offset);
        return true;
    }
}
