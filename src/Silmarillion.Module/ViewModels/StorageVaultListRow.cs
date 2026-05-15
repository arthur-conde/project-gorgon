using StorageVaultPoco = Mithril.Reference.Models.Misc.StorageVault;

namespace Silmarillion.ViewModels;

/// <summary>
/// Master-list row projection for the StorageVaults tab. The raw
/// <see cref="StorageVaultPoco"/> doesn't carry the envelope key (the selection / deep-link
/// contract) nor the derived account-wide flag or effective-slot summary, so — like
/// Recipes / Lorebooks — the tab projects a row record rather than binding the POCO.
/// <para>
/// <see cref="EnvelopeKey"/> is the selection key (matches the
/// <see cref="Mithril.Shared.Reference.EntityRef.StorageVault(string)"/> factory and the
/// <c>StorageVaults</c> service contract). It is the operator NPC's internal name, or a
/// <c>"*"</c>-prefixed account-wide form (transfer chest). The <c>MithrilQueryBox</c>
/// schema is reflected from this record, so every queryable facet (name, area key,
/// grouping, account-wide flag, slot summary) must be a public property here.
/// </para>
/// </summary>
public sealed record StorageVaultListRow(
    StorageVaultPoco Vault,
    string EnvelopeKey,
    string DisplayName,
    string? AreaKey,
    string? Grouping,
    bool IsAccountWide,
    string SlotSummary);
