using System.Globalization;
using System.Text;
using Legolas.Flow;
using Legolas.ViewModels;
using Mithril.Shared.Character;
using Mithril.Shared.Reference;

namespace Legolas.Sharing;

/// <summary>
/// Snapshots the active <see cref="SessionState"/> when the survey FSM reaches
/// <see cref="SurveyFlowState.Done"/> and exposes the result for the share dialog.
///
/// The snapshot has to happen at the transition itself: <see cref="LegolasSettings.AutoResetWhenAllCollected"/>
/// is true by default, and that immediately calls
/// <see cref="SurveyFlowController.Reset"/> after the Done transition fires —
/// which empties <c>SessionState.Surveys</c> and <c>SessionState.CollectedItems</c>.
/// We hook <c>Transitioned</c>, which fires synchronously inside <c>TransitionTo</c>
/// before the auto-reset branch runs, so the data is still intact.
///
/// The session collects items by display name (the chat parser only sees prose);
/// we resolve to <see cref="ItemEntry.InternalName"/> at snapshot time via
/// <see cref="IReferenceDataService"/> so the wire payload is CDN-version- and
/// locale-stable. Items that don't resolve fall through to
/// <see cref="LegolasSharePayload.UnknownByName"/> rather than being dropped.
/// </summary>
public sealed class LegolasReportService
{
    private readonly SessionState _session;
    private readonly IActiveCharacterService? _activeChar;
    private readonly IReferenceDataService? _refData;
    private readonly TimeProvider _clock;
    private Dictionary<string, ItemEntry>? _byDisplayName;

    public LegolasReportService(
        SurveyFlowController flow,
        SessionState session,
        TimeProvider? clock = null,
        IActiveCharacterService? activeChar = null,
        IReferenceDataService? refData = null)
    {
        _session = session;
        _activeChar = activeChar;
        _refData = refData;
        _clock = clock ?? TimeProvider.System;
        flow.Transitioned += OnTransitioned;
    }

    /// <summary>
    /// Most recent end-of-run snapshot. Survives subsequent FSM resets, so the
    /// "View last report" button can re-open the dialog after Auto-reset has
    /// already cleared <c>SessionState</c>.
    /// </summary>
    public LegolasSharePayload? LatestReport { get; private set; }

    /// <summary>Fires (UI thread) when a new report is built.</summary>
    public event Action<LegolasSharePayload>? ReportGenerated;

    public LegolasSharePayload BuildPayload(bool includeCharacterName)
    {
        var resolved = new Dictionary<string, int>(StringComparer.Ordinal);
        Dictionary<string, int>? unknown = null;

        foreach (var (displayName, count) in _session.CollectedItems)
        {
            if (TryResolveByDisplayName(displayName, out var entry))
            {
                resolved.TryGetValue(entry.InternalName, out var existing);
                resolved[entry.InternalName] = existing + count;
            }
            else
            {
                unknown ??= new Dictionary<string, int>(StringComparer.Ordinal);
                unknown.TryGetValue(displayName, out var existing);
                unknown[displayName] = existing + count;
            }
        }

        return new LegolasSharePayload
        {
            CharacterName = includeCharacterName ? _activeChar?.ActiveCharacterName : null,
            StartedAt = _session.StartedAt ?? _clock.GetUtcNow(),
            CompletedAt = _clock.GetUtcNow(),
            Mode = _session.Mode,
            SurveyCount = _session.Surveys.Count,
            CollectedItemsByInternalName = resolved,
            UnknownByName = unknown,
        };
    }

    private bool TryResolveByDisplayName(string displayName, out ItemEntry entry)
    {
        entry = null!;
        if (_refData is null) return false;
        // Lazy-build a display-name index on first need. The reference data set is
        // bounded (~thousand items); a full scan would also work but the index
        // keeps repeated lookups O(1) within a single payload build.
        if (_byDisplayName is null)
        {
            var index = new Dictionary<string, ItemEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _refData.Items.Values)
            {
                // First-match-wins: ambiguous display names (rare for survey items)
                // resolve to whichever one we see first, which is a presentational
                // best-effort. Recipients re-resolve from their own catalog anyway.
                index.TryAdd(item.Name, item);
            }
            _byDisplayName = index;
        }
        if (_byDisplayName.TryGetValue(displayName, out var found))
        {
            entry = found;
            return true;
        }
        return false;
    }

    private void OnTransitioned(SurveyTransition t)
    {
        if (t.To != SurveyFlowState.Done) return;
        // v1 scope: Survey mode only. Motherlode runs aren't meaningfully summarisable
        // (they end with a single dig) — defer to a future enhancement if desired.
        if (_session.Mode != SessionMode.Survey) return;

        var payload = BuildPayload(includeCharacterName: true);
        LatestReport = payload;
        ReportGenerated?.Invoke(payload);
    }

    /// <summary>
    /// Discord/forum-friendly text summary. The optional <paramref name="refData"/>
    /// is used to resolve <c>InternalName</c> keys into display names; fall back to
    /// the InternalName itself when no resolver is supplied or the item isn't in
    /// the local catalog.
    /// </summary>
    public static string BuildSummary(LegolasSharePayload payload, IReferenceDataService? refData = null)
    {
        var sb = new StringBuilder();
        var who = string.IsNullOrWhiteSpace(payload.CharacterName)
            ? "Anonymous"
            : payload.CharacterName!;
        sb.AppendLine($"{who} — Survey run complete");

        var elapsed = payload.CompletedAt - payload.StartedAt;
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "Started {0} · finished {1} · elapsed {2}",
            payload.StartedAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture),
            payload.CompletedAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture),
            FormatElapsed(elapsed)));

        sb.AppendLine($"Surveys collected: {payload.SurveyCount}");

        var lines = new List<(string Display, int Count)>();
        foreach (var (internalName, count) in payload.CollectedItemsByInternalName)
        {
            var display = (refData is not null && refData.ItemsByInternalName.TryGetValue(internalName, out var entry))
                ? entry.Name
                : internalName;
            lines.Add((display, count));
        }
        if (payload.UnknownByName is { Count: > 0 } unk)
        {
            foreach (var (name, count) in unk)
                lines.Add((name, count));
        }

        if (lines.Count > 0)
        {
            sb.AppendLine("Items earned:");
            foreach (var (display, count) in lines
                         .OrderByDescending(l => l.Count)
                         .ThenBy(l => l.Display, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"  • {display} ×{count}");
            }
        }
        else
        {
            sb.AppendLine("Items earned: (none recorded)");
        }

        return sb.ToString().TrimEnd();
    }

    public static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 0) elapsed = TimeSpan.Zero;
        if (elapsed.TotalHours >= 1)
            return string.Format(CultureInfo.InvariantCulture, "{0:0}h {1:0}m {2:0}s",
                Math.Floor(elapsed.TotalHours), elapsed.Minutes, elapsed.Seconds);
        if (elapsed.TotalMinutes >= 1)
            return string.Format(CultureInfo.InvariantCulture, "{0:0}m {1:0}s",
                Math.Floor(elapsed.TotalMinutes), elapsed.Seconds);
        return string.Format(CultureInfo.InvariantCulture, "{0:0}s", elapsed.TotalSeconds);
    }
}
