using System.Globalization;

namespace Arda.Ingest.Clock;

/// <summary>
/// <see cref="ILogSourceClock"/> for chat logs. Parses the
/// <c>yy-MM-dd HH:mm:ss\t</c> prefix (18 characters: 2-digit year, dash,
/// 2-digit month, dash, 2-digit day, space, 2-digit hour, colon, 2-digit
/// minute, colon, 2-digit second, tab).
/// <para>
/// The prefix is in <b>host-local time</b> — the game writes wall-clock
/// with its local timezone. The clock folds the parsed <see cref="DateTime"/>
/// into a <see cref="DateTimeOffset"/> using the configured timezone offset.
/// </para>
/// <para>
/// When a chat login banner provides the originating session's
/// <c>Timezone Offset</c>, the coordinator calls <see cref="SetOffset"/> to
/// update. Until then, the constructor-injected <see cref="TimeZoneInfo"/>
/// provides the local offset for each parsed instant.
/// </para>
/// </summary>
internal sealed class ChatLogClock : ILogSourceClock
{
    private readonly TimeZoneInfo _timeZone;
    private TimeSpan? _overrideOffset;

    internal const int PrefixLength = 18; // "yy-MM-dd HH:mm:ss\t"

    public ChatLogClock(TimeZoneInfo? timeZone = null)
    {
        _timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    /// <inheritdoc/>
    public ClockResult TryParse(ReadOnlySpan<char> line)
    {
        if (line.Length < PrefixLength)
            return ClockResult.None;

        // Structural check: yy-MM-dd HH:mm:ss\t
        if (line[2] != '-' || line[5] != '-' || line[8] != ' ' ||
            line[11] != ':' || line[14] != ':' || line[17] != '\t')
            return ClockResult.None;

        if (!int.TryParse(line[..2], NumberStyles.None, null, out var year) ||
            !int.TryParse(line[3..5], NumberStyles.None, null, out var month) ||
            !int.TryParse(line[6..8], NumberStyles.None, null, out var day) ||
            !int.TryParse(line[9..11], NumberStyles.None, null, out var hour) ||
            !int.TryParse(line[12..14], NumberStyles.None, null, out var minute) ||
            !int.TryParse(line[15..17], NumberStyles.None, null, out var second))
            return ClockResult.None;

        if (month < 1 || month > 12 || day < 1 || day > 31 ||
            hour > 23 || minute > 59 || second > 59)
            return ClockResult.None;

        var localDateTime = new DateTime(2000 + year, month, day, hour, minute, second);

        var offset = _overrideOffset ?? _timeZone.GetUtcOffset(localDateTime);
        var timestamp = new DateTimeOffset(localDateTime, offset);

        return new ClockResult(timestamp, PrefixLength);
    }

    /// <inheritdoc/>
    public void EnsureAnchored(
        ReadOnlySpan<(int Start, int Length)> firstBatchLines,
        char[] buffer,
        Func<DateTime> mtimeUtcAccessor)
    {
        // Chat logs carry the full date on every line — no anchoring needed.
    }

    /// <inheritdoc/>
    public void TryConsumeBanner(ReadOnlySpan<char> line)
    {
        // Chat-log timezone-offset banners are handled by
        // ChatLogSource.TryApplyBannerOffset on the assembled line string.
    }

    /// <summary>
    /// Override the UTC offset used for timestamp conversion. Called by
    /// <see cref="Coordinator.ChatLogSource"/> when it observes a login banner
    /// with a <c>Timezone Offset</c> field. This is an L0 concern: the offset
    /// is a clock-calibration signal extracted at the source layer before
    /// lines enter the classifier.
    /// </summary>
    internal void SetOffset(TimeSpan utcOffset)
    {
        _overrideOffset = utcOffset;
    }
}
