using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Reference.Models.Abilities;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using Mithril.Shared.Wpf.Query;

namespace Silmarillion.ViewModels;

/// <summary>
/// Abilities master-detail view-model. Mirrors <see cref="ItemsTabViewModel"/> /
/// <see cref="RecipesTabViewModel"/> / <see cref="NpcsTabViewModel"/>'s shape: filterable
/// row list on the left, ability detail on the right. On selection change builds an
/// <see cref="AbilityDetailViewModel"/> with skill / group / cost / prerequisite / PvE /
/// conditional / ammo / sources cross-link projections resolved from
/// <see cref="IReferenceDataService"/>.
///
/// Subscribes to <see cref="IReferenceDataService.FileUpdated"/> for <c>"abilities"</c>,
/// <c>"sources_abilities"</c>, and <c>"npcs"</c> — the last because trainer-source chip
/// resolution reads NPC display names. Rebuilds <see cref="AllAbilities"/> on the UI thread,
/// preserving the current selection by <see cref="AbilityListRow.InternalName"/>.
/// </summary>
public sealed partial class AbilitiesTabViewModel : ObservableObject, ITabViewModel
{
    /// <summary>
    /// Reflected schema for <see cref="AbilityListRow"/> exposed to <c>MithrilQueryBox.Schema</c>
    /// so the query box can offer completion and highlight known column names. <c>QueryFilter</c>
    /// on the bound ListBox reflects the same surface from the row type at attach time, so the
    /// suggestions stay in sync with what actually filters.
    /// </summary>
    public static IReadOnlyList<ColumnSchema> SchemaSnapshot { get; } =
        ColumnBindingHelper.ToSchema(ColumnBindingHelper.BuildFromProperties(typeof(AbilityListRow)));

    public string TabHeader => "Abilities";
    public int TabOrder => 4;

    private readonly IReferenceDataService _refData;
    private readonly IReferenceNavigator _navigator;
    private readonly IEntityNameResolver _nameResolver;
    private readonly RelayCommand<EntityRef?> _openEntityCommand;

    public AbilitiesTabViewModel(IReferenceDataService refData, IReferenceNavigator navigator, IEntityNameResolver nameResolver)
    {
        _refData = refData;
        _navigator = navigator;
        _nameResolver = nameResolver;
        _openEntityCommand = new RelayCommand<EntityRef?>(r => { if (r is not null) _navigator.Open(r); });
        _allAbilities = BuildAllAbilities(refData);
        refData.FileUpdated += OnFileUpdated;
    }

    [ObservableProperty]
    private IReadOnlyList<AbilityListRow> _allAbilities;

    [ObservableProperty]
    private string _queryText = "";

    [ObservableProperty]
    private string? _queryError;

    /// <summary>
    /// ListBox-bound selection. Setting it from an <see cref="Ability"/> POCO (in tests or via the
    /// navigator) resolves to the matching row.
    /// </summary>
    [ObservableProperty]
    private AbilityListRow? _selectedRow;

    [ObservableProperty]
    private AbilityDetailViewModel? _detailViewModel;

    partial void OnSelectedRowChanged(AbilityListRow? value)
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
        // abilities.json drives the master list. sources_abilities.json feeds the Sources block
        // on the detail pane. npcs.json affects trainer-chip display names. Rebuild on any of
        // them so an open detail re-resolves chips against the fresh refData snapshot.
        if (fileKey is not ("abilities" or "sources_abilities" or "npcs"))
            return;

        UiThread.Run(() =>
        {
            var captured = SelectedRow?.InternalName;
            if (fileKey == "abilities")
            {
                AllAbilities = BuildAllAbilities(_refData);
            }
            if (!string.IsNullOrEmpty(captured))
            {
                var resolved = AllAbilities.FirstOrDefault(r => r.InternalName == captured);
                // Toggle through null to force OnSelectedRowChanged to rebuild the detail VM
                // with the fresh refData snapshot (cross-link chip resolution reads live refData).
                SelectedRow = null;
                SelectedRow = resolved;
            }
        });
    }

    private IReadOnlyList<AbilityListRow> BuildAllAbilities(IReferenceDataService refData) =>
        refData.AbilitiesByInternalName
            .Where(kv => !string.IsNullOrEmpty(kv.Value.InternalName))
            .Select(kv => BuildRow(kv.Key, kv.Value))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private AbilityListRow BuildRow(string internalName, Ability ability)
    {
        var name = string.IsNullOrEmpty(ability.Name) ? internalName : ability.Name!;
        var skill = ResolveSkillDisplay(ability.Skill);
        var keywords = (ability.Keywords ?? (IReadOnlyList<string>)[])
            .Where(k => !string.IsNullOrEmpty(k))
            .Select(k => new IngredientKeywordValue(k))
            .ToList();
        // Surface EffectKeywordReqs as a CONTAINS-queryable collection so a user can
        // hand-filter the Abilities tab by `EffectKeywordReqs CONTAINS "<tag>"`. (This
        // also backed the retired AbilityByEffectKeyword synthetic deep link, removed in
        // #318 — the effect->abilities surface now opens a provenance popup fed the index
        // directly, no query re-derivation.)
        var effectKeywordReqs = (ability.EffectKeywordReqs ?? (IReadOnlyList<string>)[])
            .Where(k => !string.IsNullOrEmpty(k))
            .Select(k => new IngredientKeywordValue(k))
            .ToList();

        return new AbilityListRow(
            Ability: ability,
            InternalName: internalName,
            Name: name,
            Skill: skill,
            Level: ability.Level,
            Rank: ability.Rank,
            Keywords: keywords,
            EffectKeywordReqs: effectKeywordReqs,
            ResetTimeSeconds: ability.ResetTime,
            IconID: ability.IconID);
    }

    private string ResolveSkillDisplay(string? skillKey)
    {
        if (string.IsNullOrEmpty(skillKey)) return "(unknown)";
        if (_refData.Skills.TryGetValue(skillKey, out var s) && !string.IsNullOrEmpty(s.DisplayName))
            return s.DisplayName;
        return skillKey;
    }

    private AbilityDetailViewModel BuildDetailViewModel(AbilityListRow row) =>
        new AbilityDetailViewModel(
            row.Ability,
            row.InternalName,
            _refData,
            _navigator,
            _nameResolver,
            _openEntityCommand);
}
