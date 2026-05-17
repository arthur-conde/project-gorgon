using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf.Query;

namespace Silmarillion.ViewModels;

/// <summary>
/// Treasure-System master-detail tab (#412 / #435). One unified catalog over both
/// Treasure entity kinds — <see cref="TreasureRowKind.Power"/> (tsysclientinfo, the
/// primary content) and <see cref="TreasureRowKind.Profile"/> (tsysprofiles pools) —
/// so a single browse list serves both #214 deep-link targets (Power-select /
/// pool-query). The right pane is polymorphic on the selected row's kind:
/// <see cref="PowerDetailViewModel"/> or <see cref="ProfileDetailViewModel"/>.
/// <para>
/// Silmarillion is a browser, not a calculator: this tab shows what the Treasure
/// System is <em>composed of</em> — no roll-resolution UI, crystal-free,
/// <c>TreasureCartography</c> excluded (that is a different system).
/// </para>
/// </summary>
public sealed partial class TreasureTabViewModel : ObservableObject, ITabViewModel
{
    /// <summary>Reflected <see cref="TreasureListRow"/> schema for <c>MithrilQueryBox</c>
    /// completion + column highlighting (cookbook step 6).</summary>
    public static IReadOnlyList<ColumnSchema> SchemaSnapshot { get; } =
        ColumnBindingHelper.ToSchema(ColumnBindingHelper.BuildFromProperties(typeof(TreasureListRow)));

    public string TabHeader => "Treasure";
    public int TabOrder => 10;

    private readonly IReferenceDataService _refData;
    private readonly IReferenceNavigator _navigator;
    private readonly IEntityNameResolver _nameResolver;
    private readonly RelayCommand<EntityRef?> _openEntityCommand;

    public TreasureTabViewModel(
        IReferenceDataService refData,
        IReferenceNavigator navigator,
        IEntityNameResolver nameResolver)
    {
        _refData = refData;
        _navigator = navigator;
        _nameResolver = nameResolver;
        _openEntityCommand = new RelayCommand<EntityRef?>(r => { if (r is not null) _navigator.Open(r); });
        _allRows = BuildAllRows(refData, nameResolver);
        refData.FileUpdated += OnFileUpdated;
    }

    [ObservableProperty]
    private IReadOnlyList<TreasureListRow> _allRows;

    [ObservableProperty]
    private string _queryText = "";

    [ObservableProperty]
    private string? _queryError;

    [ObservableProperty]
    private TreasureListRow? _selectedRow;

    /// <summary>
    /// Polymorphic detail VM — a <see cref="PowerDetailViewModel"/> or a
    /// <see cref="ProfileDetailViewModel"/> resolved by the view's per-type DataTemplate.
    /// </summary>
    [ObservableProperty]
    private object? _detailViewModel;

    partial void OnSelectedRowChanged(TreasureListRow? value)
    {
        if (value is null)
        {
            DetailViewModel = null;
            return;
        }
        DetailViewModel = BuildDetail(value);
    }

    private object? BuildDetail(TreasureListRow row) => row.Kind switch
    {
        TreasureRowKind.Power when _refData.Powers.TryGetValue(row.InternalName, out var power) =>
            new PowerDetailViewModel(power, _refData, _navigator, _nameResolver, _openEntityCommand),
        TreasureRowKind.Profile when _refData.Profiles.ContainsKey(row.InternalName) =>
            new ProfileDetailViewModel(row.InternalName, _refData, _navigator, _nameResolver, _openEntityCommand),
        _ => null,
    };

    private void OnFileUpdated(object? sender, string fileKey)
    {
        // tsysclientinfo / tsysprofiles drive the master list. items / recipes only
        // affect the open detail's pool / recipe cross-links (which resolve through the
        // ProfilesByPower / ItemsByTSysProfile / RecipesByProducedItem indices), so a
        // refresh of any of the four rebuilds the open detail; the list itself only
        // rebuilds for the two it's sourced from.
        if (fileKey is not ("tsysclientinfo" or "tsysprofiles" or "items" or "recipes"))
            return;

        UiThread.Run(() =>
        {
            var captured = SelectedRow;
            if (fileKey is "tsysclientinfo" or "tsysprofiles")
                AllRows = BuildAllRows(_refData, _nameResolver);

            if (captured is not null)
            {
                var resolved = AllRows.FirstOrDefault(
                    r => r.Kind == captured.Kind
                         && string.Equals(r.InternalName, captured.InternalName, StringComparison.Ordinal));
                SelectedRow = null;
                SelectedRow = resolved;
            }
        });
    }

    private static IReadOnlyList<TreasureListRow> BuildAllRows(
        IReferenceDataService refData,
        IEntityNameResolver nameResolver)
    {
        var rows = new List<TreasureListRow>(refData.Powers.Count + refData.Profiles.Count);

        // Pools first (40) — the small, navigationally-central set sits at the top.
        foreach (var (profileName, powers) in refData.Profiles
                     .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new TreasureListRow(
                Kind: TreasureRowKind.Profile,
                InternalName: profileName,
                Name: profileName,
                KindLabel: "Pool",
                Skill: null,
                Secondary: "Pool",
                TierCount: 0,
                PowerCount: powers?.Count ?? 0));
        }

        foreach (var power in refData.Powers.Values
                     .OrderBy(p => p.InternalName, StringComparer.OrdinalIgnoreCase))
        {
            var skillDisplay = string.IsNullOrEmpty(power.Skill)
                ? null
                : (refData.Skills.TryGetValue(power.Skill, out var s) && !string.IsNullOrEmpty(s.DisplayName)
                    ? s.DisplayName
                    : power.Skill);
            rows.Add(new TreasureListRow(
                Kind: TreasureRowKind.Power,
                // Q1: InternalName verbatim is the power's identity (no DisplayName exists).
                InternalName: power.InternalName,
                Name: nameResolver.Resolve(EntityRef.Power(power.InternalName)),
                KindLabel: "Power",
                Skill: skillDisplay,
                Secondary: skillDisplay ?? "Power",
                TierCount: power.Tiers?.Count ?? 0,
                PowerCount: 0));
        }

        return rows;
    }
}
