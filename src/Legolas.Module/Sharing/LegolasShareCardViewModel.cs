using System.Globalization;
using System.Windows.Media.Imaging;
using Mithril.Shared.Icons;
using Mithril.Shared.Reference;

namespace Legolas.Sharing;

/// <summary>
/// View model for the Legolas social-card share image. Built from a
/// <see cref="LegolasSharePayload"/>; bitmap icons resolve through
/// <see cref="IIconCacheService"/> at construction time so the off-screen
/// render in <see cref="LegolasShareCardRenderer"/> doesn't depend on the
/// IconImage Loaded lifecycle (which doesn't fire for unparented visuals).
/// </summary>
public sealed class LegolasShareCardViewModel
{
    public const double CardWidth = 1000;
    // The card auto-grows vertically with the items WrapPanel; CardMinHeight keeps
    // sparse runs (1–2 items) from looking dinky. Fixed-height layout is gone — see
    // the renderer's two-pass measure for the auto-height pipeline.
    public const double CardMinHeight = 320;

    public LegolasShareCardViewModel(
        LegolasSharePayload payload,
        IReferenceDataService? refData,
        IIconCacheService? iconCache)
    {
        var hasName = !string.IsNullOrWhiteSpace(payload.CharacterName);
        CharacterTitle = hasName ? payload.CharacterName! : "Legolas · Survey";
        HasIdentity = hasName;
        SubtitleText = "Survey run complete";

        var elapsed = payload.CompletedAt - payload.StartedAt;
        ElapsedText = LegolasReportService.FormatElapsed(elapsed);
        SurveyCountText = payload.SurveyCount == 1
            ? "1 survey"
            : string.Format(CultureInfo.InvariantCulture, "{0} surveys", payload.SurveyCount);

        StartedText = string.Format(CultureInfo.InvariantCulture,
            "Started {0:g}", payload.StartedAt.LocalDateTime);
        CompletedText = string.Format(CultureInfo.InvariantCulture,
            "Finished {0:g}", payload.CompletedAt.LocalDateTime);

        // Build all rows up-front — resolved (InternalName-keyed) and unknown (display-name-keyed)
        // — then take the top N by count for display. Resolved entries get an icon; unknowns
        // render name-only.
        var all = new List<LegolasShareItem>();
        foreach (var (internalName, count) in payload.CollectedItemsByInternalName)
        {
            string display = internalName;
            BitmapImage? icon = null;
            if (refData is not null && refData.ItemsByInternalName.TryGetValue(internalName, out var entry))
            {
                display = entry.Name ?? internalName;
                if (iconCache is not null && entry.IconId > 0)
                    icon = iconCache.GetOrLoadIcon(entry.IconId);
            }
            all.Add(new LegolasShareItem(display, count, icon));
        }
        if (payload.UnknownByName is { Count: > 0 } unknown)
        {
            foreach (var (name, count) in unknown)
                all.Add(new LegolasShareItem(name, count, null));
        }

        Items = all
            .OrderByDescending(i => i.Count)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxItemsOnCard)
            .ToList();
    }

    private const int MaxItemsOnCard = 12;

    public string CharacterTitle { get; }
    public bool HasIdentity { get; }
    public string SubtitleText { get; }
    public string ElapsedText { get; }
    public string SurveyCountText { get; }
    public string StartedText { get; }
    public string CompletedText { get; }
    public IReadOnlyList<LegolasShareItem> Items { get; }
}

public sealed record LegolasShareItem(string Name, int Count, BitmapImage? Icon)
{
    public string CountText => $"×{Count}";
    public bool HasIcon => Icon is not null;
}
