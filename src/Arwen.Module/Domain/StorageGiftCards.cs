using Mithril.Reference.Models.Npcs;

namespace Arwen.Domain;

/// <summary>
/// One item in storage that has at least one accepting NPC. Carries its own
/// pre-projected recipient list so the detail pane is instant on selection.
/// </summary>
public sealed record StorageItemCard(
    int TypeId,
    int IconId,
    string Name,
    int StackSize,
    string Vault,
    int RecipientCount,
    bool HasLove,
    IReadOnlyList<RecipientCard> AllRecipients);

/// <summary>
/// One NPC who would accept a particular item. Mirrors the favor-projection
/// shape used by <see cref="ViewModels.GiftScannerRow"/> so the same
/// "Favor After Gift" XAML pattern renders both views.
/// </summary>
public sealed record RecipientCard(
    string NpcKey,
    string NpcName,
    string NpcArea,
    string Desire,
    double RelativeScore,
    // Favor projection (null when calibration data is unavailable):
    double? EstimatedFavor,        // per-stack total favor
    string? EstimateSource,        // calibration tier (Item / Signature / NPC / Global)
    int EstimateSamples,
    double? CurrentFavor,           // null when player hasn't met the NPC
    double? ProjectedFavor,
    FavorTier CurrentTier,
    FavorTier ProjectedTier,
    double CurrentTierCeiling,
    double CurrentProgressFraction,
    double ProjectedProgressFraction);

/// <summary>Detail-pane group: all recipients in a single NPC area.</summary>
public sealed record RecipientAreaGroup(
    string Area,
    IReadOnlyList<RecipientCard> Recipients);
