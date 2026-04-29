namespace Mithril.Reference.Models.Quests;

/// <summary>
/// Polymorphic entry in a quest's <c>Rewards</c> array. The JSON's <c>T</c>
/// field discriminates between 9 concrete subclasses. Unknown discriminators
/// deserialize to <see cref="UnknownQuestReward"/>.
/// </summary>
public abstract class QuestReward
{
    public string T { get; set; } = "";
}

/// <summary>Sentinel for any <c>T</c> value not covered by a concrete subclass.</summary>
public sealed class UnknownQuestReward : QuestReward, IUnknownDiscriminator
{
    public string DiscriminatorValue { get; set; } = "";
}

public sealed class SkillXpReward : QuestReward
{
    public string? Skill { get; set; }
    public int? Xp { get; set; }
}

public sealed class WorkOrderCurrencyReward : QuestReward
{
    public int? Amount { get; set; }
    public string? Currency { get; set; }
}

public sealed class CurrencyReward : QuestReward
{
    public int? Amount { get; set; }
    public string? Currency { get; set; }
}

public sealed class RecipeReward : QuestReward
{
    public string? Recipe { get; set; }
}

public sealed class CombatXpReward : QuestReward
{
    public int? Level { get; set; }
    public int? Xp { get; set; }
}

public sealed class GuildXpReward : QuestReward
{
    public int? Xp { get; set; }
}

public sealed class GuildCreditsReward : QuestReward
{
    public int? Credits { get; set; }
}

public sealed class RacingXpReward : QuestReward
{
    public string? Skill { get; set; }
    public int? Xp { get; set; }
}

public sealed class AbilityReward : QuestReward
{
    public string? Ability { get; set; }
}
