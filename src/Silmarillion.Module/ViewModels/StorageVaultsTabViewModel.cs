using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using Mithril.Shared.Wpf.Query;
using StorageVaultPoco = Mithril.Reference.Models.Misc.StorageVault;

namespace Silmarillion.ViewModels;

/// <summary>
/// StorageVaults master-detail view-model. Small dataset (≈92 entries — no virtualization
/// concern). Row type is <see cref="StorageVaultListRow"/> because the raw
/// <see cref="StorageVaultPoco"/> doesn't carry the envelope key (selection contract), the
/// account-wide flag (derived from the <c>"*"</c> prefix) nor the effective-slot summary.
/// <para>
/// Subscribes to <see cref="IReferenceDataService.FileUpdated"/> for
/// <c>"storagevaults"</c>; selection preserved by <see cref="StorageVaultListRow.EnvelopeKey"/>.
/// All #249 cross-links are 1:1 chips resolved lazily in the detail VM, so the tab depends
/// only on its own source file.
/// </para>
/// </summary>
public sealed partial class StorageVaultsTabViewModel : ObservableObject, ITabViewModel
{
    /// <summary>
    /// Reflected schema for <see cref="StorageVaultListRow"/> exposed to
    /// <c>MithrilQueryBox.Schema</c> so the query box offers completion / highlights known
    /// columns (DisplayName / AreaKey / Grouping / IsAccountWide / SlotSummary / …).
    /// </summary>
    public static IReadOnlyList<ColumnSchema> SchemaSnapshot { get; } =
        ColumnBindingHelper.ToSchema(ColumnBindingHelper.BuildFromProperties(typeof(StorageVaultListRow)));

    public string TabHeader => "Vaults";
    public int TabOrder => 8;

    private readonly IReferenceDataService _refData;
    private readonly IReferenceNavigator _navigator;
    private readonly IEntityNameResolver _nameResolver;

    public StorageVaultsTabViewModel(
        IReferenceDataService refData,
        IReferenceNavigator navigator,
        IEntityNameResolver nameResolver,
        Silmarillion.SilmarillionSettings settings)
    {
        _refData = refData;
        _navigator = navigator;
        _nameResolver = nameResolver;
        _ = settings; // reserved for parity with the cookbook ctor shape; no setting read yet
        OpenEntityCommand = new RelayCommand<EntityRef?>(r => { if (r is not null) _navigator.Open(r); });
        _allVaults = BuildAllVaults(refData, nameResolver);
        refData.FileUpdated += OnFileUpdated;
    }

    public RelayCommand<EntityRef?> OpenEntityCommand { get; }

    [ObservableProperty]
    private IReadOnlyList<StorageVaultListRow> _allVaults;

    [ObservableProperty]
    private string _queryText = "";

    [ObservableProperty]
    private string? _queryError;

    [ObservableProperty]
    private StorageVaultListRow? _selectedVault;

    [ObservableProperty]
    private StorageVaultDetailViewModel? _detailViewModel;

    partial void OnSelectedVaultChanged(StorageVaultListRow? value)
    {
        DetailViewModel = value is null ? null : BuildDetailViewModel(value);
    }

    private void OnFileUpdated(object? sender, string fileKey)
    {
        if (fileKey != "storagevaults") return;

        UiThread.Run(() =>
        {
            var capturedKey = SelectedVault?.EnvelopeKey;
            AllVaults = BuildAllVaults(_refData, _nameResolver);
            if (!string.IsNullOrEmpty(capturedKey))
            {
                var resolved = AllVaults.FirstOrDefault(v => v.EnvelopeKey == capturedKey);
                // Toggle through null so OnSelectedVaultChanged rebuilds the detail VM
                // against the fresh refData snapshot ([ObservableProperty] suppresses a
                // same-reference reassignment).
                SelectedVault = null;
                SelectedVault = resolved;
            }
        });
    }

    private static IReadOnlyList<StorageVaultListRow> BuildAllVaults(
        IReferenceDataService refData, IEntityNameResolver nameResolver)
    {
        return refData.StorageVaults
            .Where(kv => !string.IsNullOrEmpty(kv.Key))
            .Select(kv => ProjectRow(kv.Key, kv.Value, nameResolver))
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.EnvelopeKey, StringComparer.Ordinal)
            .ToList();
    }

    private static StorageVaultListRow ProjectRow(
        string envelopeKey, StorageVaultPoco vault, IEntityNameResolver nameResolver)
    {
        var isAccountWide = envelopeKey.StartsWith('*');
        var displayName = !string.IsNullOrEmpty(vault.NpcFriendlyName)
            ? vault.NpcFriendlyName!
            : nameResolver.Resolve(EntityRef.StorageVault(envelopeKey));

        return new StorageVaultListRow(
            Vault: vault,
            EnvelopeKey: envelopeKey,
            DisplayName: displayName,
            AreaKey: string.IsNullOrEmpty(vault.Area) ? null : vault.Area,
            Grouping: string.IsNullOrEmpty(vault.Grouping) ? null : vault.Grouping,
            IsAccountWide: isAccountWide,
            SlotSummary: SummariseSlots(vault));
    }

    /// <summary>
    /// Plain card secondary-line summary per the cookbook card convention (no unit-letter
    /// prefixes). Favor-scaled vaults show the max favor-tier slot count; flat vaults show
    /// the count; dynamic vaults show the script-atomic range; transfer chests (NumSlots:0,
    /// no Levels) show "transfer".
    /// </summary>
    private static string SummariseSlots(StorageVaultPoco vault)
    {
        if (vault.Levels is { Count: > 0 } levels)
        {
            var max = levels.Values.DefaultIfEmpty(0).Max();
            return max > 0 ? $"up to {max} slots" : "favor-scaled";
        }
        if (!string.IsNullOrEmpty(vault.NumSlotsScriptAtomic)
            && vault.NumSlotsScriptAtomicMaxValue is { } hi)
        {
            return $"up to {hi} slots";
        }
        if (vault.NumSlots is { } n && n > 0)
            return $"{n} slots";
        return "transfer";
    }

    private StorageVaultDetailViewModel BuildDetailViewModel(StorageVaultListRow row) =>
        new StorageVaultDetailViewModel(row, _refData, _navigator, _nameResolver);
}
