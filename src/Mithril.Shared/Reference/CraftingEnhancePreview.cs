namespace Mithril.Shared.Reference;

/// <summary>
/// Parsed projection of the <c>CraftingEnhance*</c>, <c>RepairItemDurability</c>,
/// <c>CraftingResetItem</c>, and <c>TransmogItemAppearance</c> entries in
/// <see cref="RecipeEntry.ResultEffects"/>. Each describes an effect the crafted
/// recipe applies to an existing item: a stat boost, a durability repair, a
/// transmog, or a reset to stock shape.
/// <para>
/// Schema across these prefixes varies — element-mod recipes carry
/// <c>(scalar, magnitude)</c>, armor/pocket recipes carry <c>(N, stackCap)</c>,
/// repair recipes carry a 5-tuple, and the reset/transmog forms are zero-arg.
/// To keep the chip uniform, each parser pre-formats the variable detail into
/// <see cref="Detail"/>; <see cref="Property"/> stays a fixed short label
/// describing what's being modified.
/// </para>
/// </summary>
public sealed record CraftingEnhancePreview(
    string Property,
    string? Detail)
{
    public string DisplayLine => string.IsNullOrEmpty(Detail)
        ? Property
        : $"{Property}: {Detail}";
}
