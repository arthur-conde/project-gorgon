namespace Celebrimbor.ViewModels;

/// <summary>
/// Minimal projection of one recipe input or output for tooltip rendering —
/// name + icon + batch quantity + optional ChanceToConsume.
/// </summary>
public sealed record IngredientChip(string Name, int IconId, int StackSize, float? ChanceToConsume);
