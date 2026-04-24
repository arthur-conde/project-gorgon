using Gorgon.Shared.Reference;

namespace Gorgon.Shared.Wpf;

/// <summary>
/// Read-only projection of an <see cref="ItemEntry"/> for <see cref="ItemDetailWindow"/>.
/// Item data is immutable within a window instance — open a new window to inspect a
/// different item rather than mutating this view-model.
/// </summary>
public sealed class ItemDetailViewModel
{
    public ItemDetailViewModel(ItemEntry item, IReferenceDataService refData)
        : this(item, refData, augments: null)
    {
    }

    public ItemDetailViewModel(ItemEntry item, IReferenceDataService refData, IReadOnlyList<AugmentPreview>? augments)
    {
        Item = item;
        EffectLines = EffectDescsRenderer.Render(item.EffectDescs, refData.Attributes);
        SkillReqChips = item.SkillReqs is null
            ? []
            : item.SkillReqs
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key} {kv.Value}")
                .ToList();
        Augments = augments ?? [];
    }

    public ItemEntry Item { get; }
    public string DisplayName => Item.Name;
    public string InternalName => Item.InternalName;
    public int IconId => Item.IconId;
    public string? EquipSlot => Item.EquipSlot;
    public string? Description => Item.Description;
    public string? FoodDesc => Item.FoodDesc;
    public IReadOnlyList<string> SkillReqChips { get; }
    public IReadOnlyList<EffectLine> EffectLines { get; }
    public IReadOnlyList<AugmentPreview> Augments { get; }
}
