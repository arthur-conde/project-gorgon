using System.Text.RegularExpressions;

namespace Mithril.GameState.Chat;

/// <summary>
/// Parses a logical chat message into its <c>(timestamp-prefix, channel, speaker, text)</c>
/// shape and classifies the channel into one of three buckets:
/// <list type="bullet">
///   <item><see cref="ChatChannelKind.Status"/> — the <c>[Status]</c> system
///   channel (inventory readouts, survey distance readouts, etc.). Consumed by
///   <see cref="Mithril.GameState.Inventory.ChatInventoryFrameProducer"/> etc.</item>
///   <item><see cref="ChatChannelKind.NpcChatter"/> — the <c>[NPC Chatter]</c>
///   channel. Drained at the producer level (no folder consumes these).</item>
///   <item><see cref="ChatChannelKind.PlayerChat"/> — every other channel.
///   Catch-all for player-typed chat: Help, Trade, Local, Whisper, Group,
///   Party, Global, plus any user-created room (e.g. <c>[woptraders]</c>).</item>
/// </list>
///
/// <para><b>Allowlist for system buckets, catch-all for player chat.</b>
/// New PG-internal system channels we don't yet recognise would route to
/// PlayerChat by default — conservative: a downstream consumer that reads
/// PlayerChat by channel allowlist still filters them out, but a downstream
/// consumer like the WoP view that scans every PlayerChat line for uppercase
/// tokens still gets the chance to observe a chat-side spoken code in a new
/// room without a code change here.</para>
/// </summary>
public static partial class ChatChannelClassifier
{
    // [Channel] Speaker: text   — the canonical chat-line shape after the
    // YY-MM-DD HH:MM:SS\t prefix (which ChatLogClock strips before we see it
    // structurally — the RawLogLine.Line carries the original including
    // prefix, so we parse it here).
    //
    // The bracketed channel can contain spaces ([NPC Chatter]) and quotes are
    // not part of channel names; `[^\]]+` greedy is sufficient.
    [GeneratedRegex(
        @"^\d{2}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\s*\t\s*\[(?<channel>[^\]]+)\]\s*(?<speaker>[^:]+?)\s*:\s*(?<text>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ChatLineRx();

    // A channel header with no speaker payload — e.g. system announcements,
    // or rare lines where the channel is a verb without a colon. Matches the
    // prefix + bracket but leaves speaker/text empty.
    [GeneratedRegex(
        @"^\d{2}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\s*\t\s*\[(?<channel>[^\]]+)\]\s*(?<text>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ChatLineNoSpeakerRx();

    /// <summary>
    /// Try to parse a physical chat line into its parts. Returns <c>true</c>
    /// when the line carries the canonical prefix-and-channel header;
    /// <c>false</c> when it doesn't (a continuation line, blank line, or
    /// unprefixed banner). Continuation-line aggregation is the producer's
    /// concern — this method classifies a single physical line only.
    /// </summary>
    public static bool TryParse(string line, out ChatLineParts parts)
    {
        parts = default;
        if (string.IsNullOrWhiteSpace(line)) return false;

        var m = ChatLineRx().Match(line);
        if (m.Success)
        {
            parts = new ChatLineParts(
                Channel: m.Groups["channel"].Value,
                Speaker: m.Groups["speaker"].Value,
                Text: m.Groups["text"].Value);
            return true;
        }

        m = ChatLineNoSpeakerRx().Match(line);
        if (m.Success)
        {
            parts = new ChatLineParts(
                Channel: m.Groups["channel"].Value,
                Speaker: string.Empty,
                Text: m.Groups["text"].Value);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Classify a channel name into a structural bucket. The check is
    /// ordinal-equal (no case folding); PG's channel names are stable
    /// fixed-case identifiers ("Status", "NPC Chatter", "Trade", …).
    /// </summary>
    public static ChatChannelKind Classify(string channel)
    {
        if (string.Equals(channel, "Status", StringComparison.Ordinal))
            return ChatChannelKind.Status;
        if (string.Equals(channel, "NPC Chatter", StringComparison.Ordinal))
            return ChatChannelKind.NpcChatter;
        return ChatChannelKind.PlayerChat;
    }
}

/// <summary>
/// Parsed shape of a physical chat line: the channel name (no brackets), the
/// speaker name (empty when the line lacks the canonical <c>Speaker:</c>
/// split), and the message text (empty when the line is a bare header).
/// </summary>
public readonly record struct ChatLineParts(string Channel, string Speaker, string Text);

/// <summary>
/// Structural channel-kind classification (#603). PlayerChat is the catch-all;
/// the two named buckets are system channels with their own folders or drain
/// policies.
/// </summary>
public enum ChatChannelKind
{
    /// <summary><c>[Status]</c> — system inventory / survey readouts.</summary>
    Status,
    /// <summary><c>[NPC Chatter]</c> — NPC ambient lines, drained.</summary>
    NpcChatter,
    /// <summary>Every other channel (player-typed chat + custom rooms).</summary>
    PlayerChat,
}
