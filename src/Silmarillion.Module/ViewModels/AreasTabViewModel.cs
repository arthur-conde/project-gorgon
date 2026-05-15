using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using Mithril.Shared.Wpf.Query;

namespace Silmarillion.ViewModels;

/// <summary>
/// Areas master-detail view-model. Row type is <see cref="AreaEntry"/> directly — the
/// slim projection is wider than the underlying <c>Area</c> POCO, so unlike most
/// Silmarillion tabs there is no <c>AreasByInternalName</c> intermediary. Documented
/// inversion of cookbook *step 1 Path 1* in the #245 handoff: when the slim projection
/// already carries everything the consumer needs, it IS the canonical surface.
/// <para>
/// Subscribes to <see cref="IReferenceDataService.FileUpdated"/> for <c>"areas"</c>
/// (rebuilds the master list), <c>"npcs"</c> and <c>"landmarks"</c> (rebuild the
/// detail VM so the NPC cluster and landmark section reflect the fresh data). Selection
/// preserved by <see cref="AreaEntry.Key"/>.
/// </para>
/// </summary>
public sealed partial class AreasTabViewModel : ObservableObject, ITabViewModel
{
    /// <summary>
    /// Reflected schema for <see cref="AreaEntry"/> exposed to <c>MithrilQueryBox.Schema</c>
    /// so the query box can offer completion (<c>Key</c> / <c>FriendlyName</c> /
    /// <c>ShortFriendlyName</c>) and highlight known column names. Sufficient for the ~36-entry
    /// area list — no derived facets needed.
    /// </summary>
    public static IReadOnlyList<ColumnSchema> SchemaSnapshot { get; } =
        ColumnBindingHelper.ToSchema(ColumnBindingHelper.BuildFromProperties(typeof(AreaEntry)));

    public string TabHeader => "Areas";
    public int TabOrder => 6;

    private readonly IReferenceDataService _refData;
    private readonly IReferenceNavigator _navigator;
    private readonly IEntityNameResolver _nameResolver;
    private readonly Silmarillion.SilmarillionSettings _settings;
    private readonly RelayCommand<EntityRef?> _openEntityCommand;

    public AreasTabViewModel(
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
        _allAreas = BuildAllAreas(refData);
        refData.FileUpdated += OnFileUpdated;
    }

    [ObservableProperty]
    private IReadOnlyList<AreaEntry> _allAreas;

    [ObservableProperty]
    private string _queryText = "";

    [ObservableProperty]
    private string? _queryError;

    [ObservableProperty]
    private AreaEntry? _selectedArea;

    [ObservableProperty]
    private AreaDetailViewModel? _detailViewModel;

    partial void OnSelectedAreaChanged(AreaEntry? value)
    {
        if (value is null)
        {
            DetailViewModel = null;
            return;
        }
        DetailViewModel = BuildDetailViewModel(value);
    }

    private void OnFileUpdated(object? sender, string fileKey)
    {
        // areas.json drives the master list; npcs.json and landmarks.json drive the
        // detail-side NPC cluster / landmark section. A refresh on any of the three
        // should rebuild the detail VM so chip/landmark data reflects the new snapshot.
        if (fileKey is not ("areas" or "npcs" or "landmarks"))
            return;

        UiThread.Run(() =>
        {
            var capturedKey = SelectedArea?.Key;
            if (fileKey == "areas")
            {
                AllAreas = BuildAllAreas(_refData);
            }
            if (!string.IsNullOrEmpty(capturedKey))
            {
                var resolved = AllAreas.FirstOrDefault(a => a.Key == capturedKey);
                // Toggle through null to force OnSelectedAreaChanged to rebuild the
                // detail VM with the fresh refData snapshot.
                SelectedArea = null;
                SelectedArea = resolved;
            }
        });
    }

    private static IReadOnlyList<AreaEntry> BuildAllAreas(IReferenceDataService refData) =>
        refData.Areas.Values
            .OrderBy(a => a.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private AreaDetailViewModel BuildDetailViewModel(AreaEntry area) =>
        new AreaDetailViewModel(
            area,
            _refData,
            _navigator,
            _nameResolver,
            _settings,
            _openEntityCommand);
}
