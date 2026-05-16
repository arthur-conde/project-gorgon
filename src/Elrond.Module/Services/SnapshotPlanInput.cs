using Mithril.Leveling;
using Mithril.Planning;
using Mithril.Shared.Character;

namespace Elrond.Services;

/// <summary>
/// Adapts a character export (<see cref="CharacterSnapshot"/>) into the
/// <i>neutral</i> multi-skill planner inputs (#225: Mithril.Leveling takes a
/// neutral <see cref="SkillState"/>/<see cref="RecipeHistory"/>; Elrond owns
/// the snapshot→neutral projection so the planner stays character-agnostic).
///
/// <para>Mirrors Celebrimbor's <c>PlanExecutor.ToSkillState/ToRecipeHistory</c>
/// by intent (Elrond must not depend on the executor module); the projection
/// is a pure field copy so the two cannot diverge in behaviour.</para>
/// </summary>
internal static class SnapshotPlanInput
{
    public static SkillState ToSkillState(CharacterSnapshot s)
        => new(s.Skills.ToDictionary(
            kv => kv.Key,
            kv => new SkillProgress(
                kv.Value.Level, kv.Value.BonusLevels,
                kv.Value.XpTowardNextLevel, kv.Value.XpNeededForNextLevel),
            StringComparer.Ordinal));

    public static RecipeHistory ToRecipeHistory(CharacterSnapshot s)
        => new(new Dictionary<string, int>(s.RecipeCompletions, StringComparer.Ordinal));

    public static PlanCharacterRef ToCharacterRef(CharacterSnapshot s)
        => PlanCharacterRef.FromSnapshot(s);
}
