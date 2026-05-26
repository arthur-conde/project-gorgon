namespace Arda.World.Player;

/// <summary>
/// A single skill entry tracked by the Player handler.
/// </summary>
public readonly record struct SkillEntry(int Raw, int Bonus, int Xp, int Tnl, int Max);

/// <summary>
/// A single recipe entry: the recipe's numeric id and its lifetime completion count.
/// </summary>
public readonly record struct RecipeEntry(int RecipeId, int Count);

/// <summary>
/// Read-only view of the player's skill and recipe state. Inject this to query
/// current values after replay completes.
/// </summary>
public interface IPlayerState
{
    /// <summary>
    /// Skills keyed by interned skill type key (e.g. "Surveying", "Tanning").
    /// </summary>
    IReadOnlyDictionary<string, SkillEntry> Skills { get; }

    /// <summary>
    /// Known recipes keyed by recipe id, with lifetime completion counts.
    /// </summary>
    IReadOnlyDictionary<int, RecipeEntry> Recipes { get; }
}
