namespace Mithril.Shared.Reference;

/// <summary>
/// Parsed projection of a <c>Give{Skill}Xp</c> entry in
/// <see cref="RecipeEntry.ResultEffects"/>. The only shipped variant today is
/// <c>GiveTeleportationXp</c> (46 occurrences); the typed shape lets future
/// <c>Give*Xp</c> prefixes flow through the same chip without parser churn.
/// </summary>
public sealed record XpGrantPreview(string Skill)
{
    public string DisplayLine => $"Grants {Skill} XP";
}
