using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Reference.Models.Quests;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using Mithril.Shared.Wpf.Query;

namespace Silmarillion.ViewModels;

/// <summary>
/// Quests master-detail view-model. Mirrors <see cref="ItemsTabViewModel"/>,
/// <see cref="RecipesTabViewModel"/>, and <see cref="NpcsTabViewModel"/>'s shape: filterable
/// row list on the left, quest detail on the right.
///
/// Subscribes to <see cref="IReferenceDataService.FileUpdated"/> for <c>"quests"</c> and
/// <c>"npcs"</c> — the latter affects the resolved <see cref="QuestListRow.FavorNpcDisplayName"/>
/// for any quest whose favor NPC changes name on a CDN refresh. Rebuilds
/// <see cref="AllQuests"/> on the UI thread, preserving the current selection by
/// <see cref="QuestListRow.InternalName"/>.
/// </summary>
public sealed partial class QuestsTabViewModel : ObservableObject, ITabViewModel
{
    /// <summary>
    /// Reflected schema for <see cref="QuestListRow"/> exposed to <c>MithrilQueryBox.Schema</c>
    /// so the query box can offer completion and highlight known column names. The same surface
    /// drives the <c>QueryFilter</c> parser at attach time.
    /// </summary>
    public static IReadOnlyList<ColumnSchema> SchemaSnapshot { get; } =
        ColumnBindingHelper.ToSchema(ColumnBindingHelper.BuildFromProperties(typeof(QuestListRow)));

    public string TabHeader => "Quests";
    public int TabOrder => 3;

    private readonly IReferenceDataService _refData;
    private readonly IReferenceNavigator _navigator;
    private readonly IEntityNameResolver _nameResolver;
    private readonly RelayCommand<EntityRef?> _openEntityCommand;

    public QuestsTabViewModel(IReferenceDataService refData, IReferenceNavigator navigator, IEntityNameResolver nameResolver)
    {
        _refData = refData;
        _navigator = navigator;
        _nameResolver = nameResolver;
        _openEntityCommand = new RelayCommand<EntityRef?>(r => { if (r is not null) _navigator.Open(r); });
        _allQuests = BuildAllQuests(refData);
        refData.FileUpdated += OnFileUpdated;
    }

    [ObservableProperty]
    private IReadOnlyList<QuestListRow> _allQuests;

    [ObservableProperty]
    private string _queryText = "";

    [ObservableProperty]
    private string? _queryError;

    /// <summary>
    /// ListBox-bound selection. Setting it via the navigator's <c>TrySelectByInternalName</c>
    /// resolves to the matching row from <see cref="AllQuests"/>.
    /// </summary>
    [ObservableProperty]
    private QuestListRow? _selectedRow;

    [ObservableProperty]
    private QuestDetailViewModel? _detailViewModel;

    partial void OnSelectedRowChanged(QuestListRow? value)
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
        // quests.json drives the master list and the detail pane. items/recipes/npcs affect
        // resolved cross-link display names and chip-navigability flags, so a refresh of any
        // of them invalidates an open detail's projection.
        if (fileKey is not ("quests" or "items" or "recipes" or "npcs"))
            return;

        UiThread.Run(() =>
        {
            var captured = SelectedRow?.InternalName;
            if (fileKey is "quests" or "npcs")
            {
                AllQuests = BuildAllQuests(_refData);
            }
            if (!string.IsNullOrEmpty(captured))
            {
                var resolved = AllQuests.FirstOrDefault(r => r.InternalName == captured);
                // Toggle through null to force OnSelectedRowChanged to rebuild the detail VM
                // with the fresh refData snapshot (cross-link chip resolution reads live refData).
                SelectedRow = null;
                SelectedRow = resolved;
            }
        });
    }

    private IReadOnlyList<QuestListRow> BuildAllQuests(IReferenceDataService refData) =>
        refData.QuestsByInternalName
            .Where(kv => !string.IsNullOrEmpty(kv.Value.InternalName))
            .Select(kv => BuildRow(kv.Key, kv.Value))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private QuestListRow BuildRow(string internalName, Quest quest)
    {
        var name = string.IsNullOrEmpty(quest.Name) ? internalName : quest.Name!;
        var favorNpcDisplay = string.IsNullOrEmpty(quest.FavorNpc)
            ? null
            : _nameResolver.Resolve(EntityRef.Npc(quest.FavorNpc!));
        var keywords = (quest.Keywords ?? (IReadOnlyList<string>)[])
            .Where(k => !string.IsNullOrEmpty(k))
            .Select(k => new QuestKeywordValue(k))
            .ToList();
        var isRepeatable = quest.ReuseTime_Days is > 0
            || quest.ReuseTime_Hours is > 0
            || quest.ReuseTime_Minutes is > 0;

        return new QuestListRow(
            Quest: quest,
            InternalName: internalName,
            Name: name,
            Level: quest.Level,
            FavorNpcDisplayName: favorNpcDisplay,
            DisplayedLocation: quest.DisplayedLocation,
            Keywords: keywords,
            IsCancellable: quest.IsCancellable ?? false,
            IsGuildQuest: quest.IsGuildQuest ?? false,
            IsWorkOrder: !string.IsNullOrEmpty(quest.WorkOrderSkill),
            IsRepeatable: isRepeatable);
    }

    private QuestDetailViewModel BuildDetailViewModel(QuestListRow row) =>
        new QuestDetailViewModel(row.Quest, row.InternalName, _refData, _navigator, _nameResolver, _openEntityCommand);
}
