using Mithril.Reference.Models.Quests;

namespace Silmarillion.ViewModels;

/// <summary>
/// Friendly repeatability bucket for a <see cref="Quest"/>. This is the <em>classification</em>
/// half of repeatability; the exact interval is carried separately as
/// <see cref="QuestCadenceClassifier.ReuseMinutes"/> so a coarse, glanceable label and a
/// precise, queryable number don't have to be the same value.
/// </summary>
public enum QuestCadence
{
    /// <summary>No reuse timer — the quest can be completed once.</summary>
    OneTime,

    /// <summary>
    /// Resets roughly once a calendar day. PG's daily quests use a deliberate ~20h
    /// cooldown (so the reset time doesn't drift later each day); the 20–24h band maps here.
    /// </summary>
    Daily,

    /// <summary>Exactly a 7-day reuse.</summary>
    Weekly,

    /// <summary>Repeatable on some other cadence (e.g. 12h, 3d, 30m).</summary>
    Other,
}

/// <summary>
/// Single home for the quest-repeatability heuristic. Both the detail-pane badge
/// (<c>QuestDetailViewModel.RepeatabilityChip</c>) and the queryable
/// <see cref="QuestListRow.Cadence"/> column read from here, so the "20–24h is Daily,
/// exactly 7d is Weekly" rule lives in exactly one tested place rather than being
/// duplicated as an inline expression per consumer.
/// </summary>
public static class QuestCadenceClassifier
{
    /// <summary>
    /// Total reuse interval in minutes, or <c>0</c> when the quest has no reuse timer.
    /// This is the precise value — unlike <see cref="Classify"/> it does not round 20h up
    /// to "a day" — so query predicates like <c>ReuseMinutes &lt;= 720</c> stay exact.
    /// </summary>
    public static int ReuseMinutes(Quest quest)
    {
        var days = quest.ReuseTime_Days ?? 0;
        var hours = quest.ReuseTime_Hours ?? 0;
        var minutes = quest.ReuseTime_Minutes ?? 0;
        return (days * 24 * 60) + (hours * 60) + minutes;
    }

    /// <summary>
    /// Buckets a quest into a friendly cadence. See <see cref="QuestCadence"/> for the
    /// rationale behind the 20–24h → <see cref="QuestCadence.Daily"/> band.
    /// </summary>
    public static QuestCadence Classify(Quest quest)
    {
        var days = quest.ReuseTime_Days ?? 0;
        var hours = quest.ReuseTime_Hours ?? 0;
        var minutes = quest.ReuseTime_Minutes ?? 0;

        if (days == 0 && hours == 0 && minutes == 0) return QuestCadence.OneTime;
        if (days == 7 && hours == 0 && minutes == 0) return QuestCadence.Weekly;
        if (days == 0 && hours is >= 20 and <= 24 && minutes == 0) return QuestCadence.Daily;
        return QuestCadence.Other;
    }

    /// <summary>
    /// Header-badge text: <c>null</c> for one-time quests (the chip collapses), the friendly
    /// name for <see cref="QuestCadence.Daily"/>/<see cref="QuestCadence.Weekly"/>, and a
    /// compact <c>"Every 3d 12h 30m"</c> form otherwise. Keeps the friendly-vs-compact
    /// decision tied to <see cref="Classify"/> so the two never disagree.
    /// </summary>
    public static string? BadgeText(Quest quest)
    {
        switch (Classify(quest))
        {
            case QuestCadence.OneTime:
                return null;
            case QuestCadence.Daily:
                return "Daily";
            case QuestCadence.Weekly:
                return "Weekly";
            default:
                var days = quest.ReuseTime_Days ?? 0;
                var hours = quest.ReuseTime_Hours ?? 0;
                var minutes = quest.ReuseTime_Minutes ?? 0;
                var parts = new List<string>(3);
                if (days > 0) parts.Add($"{days}d");
                if (hours > 0) parts.Add($"{hours}h");
                if (minutes > 0) parts.Add($"{minutes}m");
                return $"Every {string.Join(" ", parts)}";
        }
    }
}
