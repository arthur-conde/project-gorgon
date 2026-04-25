namespace Mithril.Shared.Reference;

/// <summary>
/// Parsed projection of a <c>LearnAbility(internalName)</c> entry in
/// <see cref="RecipeEntry.ResultEffects"/>. <see cref="DisplayName"/> falls
/// back to a humanised form of the internal name when no abilities reference
/// dictionary is available.
/// </summary>
public sealed record LearnedAbilityPreview(
    string AbilityInternalName,
    string DisplayName)
{
    public string DisplayLine => $"Teaches ability: {DisplayName}";
}
