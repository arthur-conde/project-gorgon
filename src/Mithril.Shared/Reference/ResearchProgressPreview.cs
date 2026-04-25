namespace Mithril.Shared.Reference;

/// <summary>
/// Parsed projection of a <c>Research{Topic}{Level}</c> entry in
/// <see cref="RecipeEntry.ResultEffects"/>. Topic shapes observed in
/// recipes.json: <c>WeatherWitching</c>, <c>FireMagic</c>, <c>IceMagic</c>,
/// <c>ExoticFireWalls</c>. Level is the trailing integer (e.g. 25 in
/// <c>ResearchFireMagic25</c>); ranges from 1 to 105 in shipped data.
/// </summary>
public sealed record ResearchProgressPreview(
    string Topic,
    int Level,
    string DisplayName)
{
    public string DisplayLine => $"{DisplayName} → Level {Level}";
}
