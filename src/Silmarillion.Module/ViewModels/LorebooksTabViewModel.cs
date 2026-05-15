using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using Mithril.Shared.Wpf.Query;
using LorebookPoco = Mithril.Reference.Models.Misc.Lorebook;

namespace Silmarillion.ViewModels;

/// <summary>
/// Lorebooks master-detail view-model. 64 books across 7 categories (tiny corpus — no
/// virtualization concern). Row type is <see cref="LorebookListRow"/> (resolved category
/// display title + has-body flag aren't on the raw POCO). Books are presented grouped by
/// category — the in-game lorebook UX precedent is grouped, and at this scale grouping
/// aids navigation rather than overwhelming. The query box still filters within the flat
/// <see cref="AllLorebooks"/> binding; the grouped view is a parallel projection over the
/// same rows (empty groups self-omit).
/// <para>
/// Subscribes to <see cref="IReferenceDataService.FileUpdated"/> for <c>"lorebooks"</c>
/// (master list), <c>"lorebookinfo"</c> (category display titles) and <c>"items"</c>
/// (rebuilds the detail-side #318 "Items that bestow this book" popup). Selection preserved
/// by <see cref="LorebookListRow.InternalName"/>.
/// </para>
/// </summary>
public sealed partial class LorebooksTabViewModel : ObservableObject, ITabViewModel
{
    /// <summary>
    /// Reflected schema for <see cref="LorebookListRow"/> exposed to
    /// <c>MithrilQueryBox.Schema</c> so the query box offers completion / highlights known
    /// columns (Title / CategoryKey / CategoryDisplayTitle / AreaKey / HasText / …).
    /// </summary>
    public static IReadOnlyList<ColumnSchema> SchemaSnapshot { get; } =
        ColumnBindingHelper.ToSchema(ColumnBindingHelper.BuildFromProperties(typeof(LorebookListRow)));

    public string TabHeader => "Lorebooks";
    public int TabOrder => 7;

    private readonly IReferenceDataService _refData;
    private readonly IReferenceNavigator _navigator;
    private readonly IEntityNameResolver _nameResolver;
    private readonly Silmarillion.SilmarillionSettings _settings;
    private readonly RelayCommand<EntityRef?> _openEntityCommand;

    public LorebooksTabViewModel(
        IReferenceDataService refData,
        IReferenceNavigator navigator,
        IEntityNameResolver nameResolver,
        Silmarillion.SilmarillionSettings settings)
    {
        _refData = refData;
        _navigator = navigator;
        _nameResolver = nameResolver;
        _settings = settings;
        _openEntityCommand = new RelayCommand<EntityRef?>(r => { if (r is not null) _navigator.Open(r); });
        _allLorebooks = BuildAllLorebooks(refData);
        _categoryGroups = BuildCategoryGroups(_allLorebooks);
        refData.FileUpdated += OnFileUpdated;
    }

    [ObservableProperty]
    private IReadOnlyList<LorebookListRow> _allLorebooks;

    [ObservableProperty]
    private IReadOnlyList<LorebookCategoryGroup> _categoryGroups;

    [ObservableProperty]
    private string _queryText = "";

    [ObservableProperty]
    private string? _queryError;

    [ObservableProperty]
    private LorebookListRow? _selectedLorebook;

    [ObservableProperty]
    private LorebookDetailViewModel? _detailViewModel;

    partial void OnSelectedLorebookChanged(LorebookListRow? value)
    {
        DetailViewModel = value is null ? null : BuildDetailViewModel(value);
    }

    private void OnFileUpdated(object? sender, string fileKey)
    {
        // lorebooks.json → master list. lorebookinfo.json → resolved CategoryDisplayTitle.
        // items.json → the detail-side ItemsBestowingLorebook popup membership.
        if (fileKey is not ("lorebooks" or "lorebookinfo" or "items"))
            return;

        UiThread.Run(() =>
        {
            var capturedName = SelectedLorebook?.InternalName;
            if (fileKey is "lorebooks" or "lorebookinfo")
            {
                AllLorebooks = BuildAllLorebooks(_refData);
                CategoryGroups = BuildCategoryGroups(AllLorebooks);
            }
            if (!string.IsNullOrEmpty(capturedName))
            {
                var resolved = AllLorebooks.FirstOrDefault(b => b.InternalName == capturedName);
                // Toggle through null so OnSelectedLorebookChanged rebuilds the detail VM
                // against the fresh refData snapshot ([ObservableProperty] suppresses a
                // same-reference reassignment).
                SelectedLorebook = null;
                SelectedLorebook = resolved;
            }
        });
    }

    private static IReadOnlyList<LorebookListRow> BuildAllLorebooks(IReferenceDataService refData)
    {
        var categories = refData.LorebookCategories;
        return refData.Lorebooks.Values
            .Where(b => !string.IsNullOrEmpty(b.InternalName))
            .Select(b => ProjectRow(b, categories))
            .OrderBy(r => r.CategoryDisplayTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static LorebookListRow ProjectRow(
        LorebookPoco book,
        IReadOnlyDictionary<string, Mithril.Reference.Models.Misc.LorebookCategoryInfo> categories)
    {
        var categoryKey = book.Category ?? "";
        var displayTitle = !string.IsNullOrEmpty(categoryKey)
                           && categories.TryGetValue(categoryKey, out var info)
                           && !string.IsNullOrEmpty(info.Title)
            ? info.Title!
            : (string.IsNullOrEmpty(categoryKey) ? "Uncategorized" : categoryKey);

        // First keyword that resolves to a known area key is the cross-link anchor; the
        // bundled corpus carries exactly one area key per book that has any.
        var areaKey = book.Keywords?
            .FirstOrDefault(k => !string.IsNullOrEmpty(k));

        return new LorebookListRow(
            Book: book,
            InternalName: book.InternalName!,
            Title: string.IsNullOrEmpty(book.Title) ? book.InternalName! : book.Title!,
            CategoryDisplayTitle: displayTitle,
            CategoryKey: categoryKey,
            AreaKey: areaKey,
            HasText: !string.IsNullOrEmpty(book.Text),
            LocationHint: string.IsNullOrEmpty(book.LocationHint) ? null : book.LocationHint);
    }

    private static IReadOnlyList<LorebookCategoryGroup> BuildCategoryGroups(
        IReadOnlyList<LorebookListRow> rows)
    {
        return rows
            .GroupBy(r => r.CategoryDisplayTitle, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new LorebookCategoryGroup(
                g.Key,
                $"{g.Key} ({g.Count()})",
                g.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
    }

    private LorebookDetailViewModel BuildDetailViewModel(LorebookListRow row) =>
        new LorebookDetailViewModel(
            row,
            _refData,
            _navigator,
            _nameResolver,
            _settings,
            _openEntityCommand);
}

/// <summary>
/// One category group on the Lorebooks master list. <paramref name="CategoryDisplayTitle"/>
/// is the resolved title (e.g. <c>"The Gods"</c>); <paramref name="Heading"/> is the
/// pre-formatted small-caps header (<c>"The Gods (22)"</c>); <paramref name="Rows"/> are
/// the books in that category, alpha by title.
/// </summary>
public sealed record LorebookCategoryGroup(
    string CategoryDisplayTitle,
    string Heading,
    IReadOnlyList<LorebookListRow> Rows);
