namespace Mithril.Shared.Reference;

/// <summary>
/// Parsed projection of a <c>DiscoverWordOfPower{N}</c> entry in
/// <see cref="RecipeEntry.ResultEffects"/>. The integer index identifies which
/// of the in-game Words of Power the recipe reveals.
/// </summary>
public sealed record WordOfPowerPreview(int Index)
{
    public string DisplayLine => $"Discovers Word of Power #{Index}";
}
