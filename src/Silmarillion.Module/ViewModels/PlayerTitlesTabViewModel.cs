using CommunityToolkit.Mvvm.ComponentModel;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using Mithril.Shared.Wpf.Query;
using PlayerTitlePoco = Mithril.Reference.Models.Misc.PlayerTitle;

namespace Silmarillion.ViewModels;

/// <summary>
/// PlayerTitles master-detail view-model. ~679 titles in a flat list (small
/// corpus — no virtualization concern). Row type is <see cref="PlayerTitleListRow"/>
/// because the raw POCO carries its label as <c>&lt;color&gt;</c>-wrapped markup
/// and no clean display string / obtainability flag.
/// <para>
/// Subscribes to <see cref="IReferenceDataService.FileUpdated"/> for
/// <c>"playertitles"</c> only — the tab has no cross-link surface (the
/// Quest→title linkage is unstructured; see the #248 NOTE in
/// <see cref="IReferenceDataService"/>), so no <c>items</c>/<c>quests</c>
/// rebuild trigger. Selection preserved by <see cref="PlayerTitleListRow.EnvelopeKey"/>.
/// </para>
/// </summary>
public sealed partial class PlayerTitlesTabViewModel : ObservableObject, ITabViewModel
{
    /// <summary>
    /// Reflected schema for <see cref="PlayerTitleListRow"/> exposed to
    /// <c>MithrilQueryBox.Schema</c> so the query box offers completion / highlights
    /// known columns (DisplayTitle / IsObtainable / HasTooltip / AccountWide / SoulWide).
    /// </summary>
    public static IReadOnlyList<ColumnSchema> SchemaSnapshot { get; } =
        ColumnBindingHelper.ToSchema(ColumnBindingHelper.BuildFromProperties(typeof(PlayerTitleListRow)));

    public string TabHeader => "Titles";
    public int TabOrder => 8;

    private readonly IReferenceDataService _refData;

    public PlayerTitlesTabViewModel(IReferenceDataService refData)
    {
        _refData = refData;
        _allTitles = BuildAllTitles(refData);
        refData.FileUpdated += OnFileUpdated;
    }

    [ObservableProperty]
    private IReadOnlyList<PlayerTitleListRow> _allTitles;

    [ObservableProperty]
    private string _queryText = "";

    [ObservableProperty]
    private string? _queryError;

    [ObservableProperty]
    private PlayerTitleListRow? _selectedTitle;

    [ObservableProperty]
    private PlayerTitleDetailViewModel? _detailViewModel;

    partial void OnSelectedTitleChanged(PlayerTitleListRow? value)
    {
        DetailViewModel = value is null ? null : new PlayerTitleDetailViewModel(value);
    }

    private void OnFileUpdated(object? sender, string fileKey)
    {
        if (fileKey != "playertitles") return;

        UiThread.Run(() =>
        {
            var capturedKey = SelectedTitle?.EnvelopeKey;
            AllTitles = BuildAllTitles(_refData);
            if (!string.IsNullOrEmpty(capturedKey))
            {
                var resolved = AllTitles.FirstOrDefault(t => t.EnvelopeKey == capturedKey);
                // Toggle through null so OnSelectedTitleChanged rebuilds the detail VM
                // against the fresh refData snapshot ([ObservableProperty] suppresses a
                // same-reference reassignment).
                SelectedTitle = null;
                SelectedTitle = resolved;
            }
        });
    }

    private static IReadOnlyList<PlayerTitleListRow> BuildAllTitles(IReferenceDataService refData)
    {
        return refData.PlayerTitles
            .Select(kv => ProjectRow(kv.Key, kv.Value))
            .OrderBy(r => r.DisplayTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.EnvelopeKey, StringComparer.Ordinal)
            .ToList();
    }

    private static PlayerTitleListRow ProjectRow(string envelopeKey, PlayerTitlePoco title)
    {
        // #248 Option A — strip the cosmetic <color> span so the list / header show
        // the clean label rather than literal markup.
        var clean = TitleColorMarkup.Strip(title.Title);
        var display = string.IsNullOrEmpty(clean) ? envelopeKey : clean!;

        // Lint_* keywords (chiefly Lint_NotObtainable) mark dev / non-earnable
        // titles. Surface obtainability as a *facet* — completionists want to see
        // the long tail, they just want to filter it.
        var isObtainable = title.Keywords is null
            || !title.Keywords.Any(k =>
                k is not null && k.StartsWith("Lint_", System.StringComparison.Ordinal));

        return new PlayerTitleListRow(
            Title: title,
            EnvelopeKey: envelopeKey,
            DisplayTitle: display,
            HasTooltip: !string.IsNullOrEmpty(title.Tooltip),
            IsObtainable: isObtainable,
            AccountWide: title.AccountWide == true,
            SoulWide: title.SoulWide == true);
    }
}
