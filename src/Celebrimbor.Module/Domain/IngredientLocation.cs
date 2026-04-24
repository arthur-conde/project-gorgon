namespace Celebrimbor.Domain;

/// <summary>Where a chunk of an ingredient lives right now.</summary>
public sealed record IngredientLocation(string Label, int Quantity);
