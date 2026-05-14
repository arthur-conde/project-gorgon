using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using Mithril.Shared.Wpf.Query;
using PocoEffect = Mithril.Reference.Models.Effects.Effect;

namespace Silmarillion.ViewModels;

/// <summary>
/// Effects master-detail view-model. Mirrors <see cref="AbilitiesTabViewModel"/>'s shape:
/// virtualized row list on the left, effect detail on the right. On selection change
/// builds an <see cref="EffectDetailViewModel"/> for the right pane.
/// <para>
/// Subscribes to <see cref="IReferenceDataService.FileUpdated"/> for <c>"effects"</c> and
/// <c>"abilities"</c> — the first drives the master list, the second feeds the on-detail
/// "Required by abilities" cross-link (rebuilt by
/// <c>ReferenceDataService.BuildEffectAbilityCrossLinkIndices</c> from both refresh paths).
/// Rebuilds <see cref="AllEffects"/> on the UI thread, preserving selection by
/// <see cref="EffectListRow.EnvelopeKey"/> — <see cref="PocoEffect.Name"/> collides across
/// many entries (e.g. multiple <c>"Riposte!"</c> effects), so the envelope key is the
/// only stable identity.
/// </para>
/// </summary>
public sealed partial class EffectsTabViewModel : ObservableObject, ITabViewModel
{
    /// <summary>
    /// Reflected schema for <see cref="EffectListRow"/> exposed to
    /// <c>MithrilQueryBox.Schema</c> so the query box can offer completion and highlight
    /// known column names. <c>QueryFilter</c> on the bound ListBox reflects the same
    /// surface from the row type at attach time, so the suggestions stay in sync.
    /// </summary>
    public static IReadOnlyList<ColumnSchema> SchemaSnapshot { get; } =
        ColumnBindingHelper.ToSchema(ColumnBindingHelper.BuildFromProperties(typeof(EffectListRow)));

    public string TabHeader => "Effects";
    public int TabOrder => 5;

    private readonly IReferenceDataService _refData;
    private readonly IReferenceNavigator _navigator;
    private readonly IEntityNameResolver _nameResolver;
    private readonly Silmarillion.SilmarillionSettings _settings;
    private readonly RelayCommand<EntityRef?> _openEntityCommand;

    public EffectsTabViewModel(
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
        _allEffects = BuildAllEffects(refData);
        refData.FileUpdated += OnFileUpdated;
    }

    [ObservableProperty]
    private IReadOnlyList<EffectListRow> _allEffects;

    [ObservableProperty]
    private string _queryText = "";

    [ObservableProperty]
    private string? _queryError;

    [ObservableProperty]
    private EffectListRow? _selectedRow;

    [ObservableProperty]
    private EffectDetailViewModel? _detailViewModel;

    partial void OnSelectedRowChanged(EffectListRow? value)
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
        // effects.json drives the master list. abilities.json doesn't change row identity
        // but DOES rebuild the on-detail "Required by abilities" cross-link via the shared
        // index — rebuild the detail VM on either refresh so chip resolution reads fresh
        // data. npcs.json doesn't affect this tab today.
        if (fileKey is not ("effects" or "abilities"))
            return;

        UiThread.Run(() =>
        {
            var captured = SelectedRow?.EnvelopeKey;
            if (fileKey == "effects")
            {
                AllEffects = BuildAllEffects(_refData);
            }
            if (!string.IsNullOrEmpty(captured))
            {
                var resolved = AllEffects.FirstOrDefault(r => r.EnvelopeKey == captured);
                // Toggle through null to force OnSelectedRowChanged to rebuild the detail VM
                // with the fresh refData snapshot.
                SelectedRow = null;
                SelectedRow = resolved;
            }
        });
    }

    private static IReadOnlyList<EffectListRow> BuildAllEffects(IReferenceDataService refData) =>
        refData.Effects
            .Where(kv => kv.Value is not null)
            .Select(kv => BuildRow(kv.Key, kv.Value))
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static EffectListRow BuildRow(string envelopeKey, PocoEffect effect)
    {
        var name = string.IsNullOrEmpty(effect.Name) ? envelopeKey : effect.Name!;
        var keywords = (effect.Keywords ?? (IReadOnlyList<string>)[])
            .Where(k => !string.IsNullOrEmpty(k))
            .Select(k => new EffectKeywordValue(k))
            .ToList();

        return new EffectListRow(
            Effect: effect,
            EnvelopeKey: envelopeKey,
            DisplayName: name,
            IconId: effect.IconId,
            StackingType: string.IsNullOrEmpty(effect.StackingType) ? null : effect.StackingType,
            Duration: string.IsNullOrEmpty(effect.Duration) ? null : effect.Duration,
            Keywords: keywords);
    }

    private EffectDetailViewModel BuildDetailViewModel(EffectListRow row) =>
        new EffectDetailViewModel(
            row.Effect,
            row.EnvelopeKey,
            _refData,
            _navigator,
            _nameResolver,
            _settings,
            _openEntityCommand);
}
