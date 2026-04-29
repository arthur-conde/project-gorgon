namespace Mithril.Reference.Models.Abilities;

/// <summary>
/// One entry in an ability's <c>ConditionalKeywords</c> list. Adds the
/// <see cref="Keyword"/> tag to the ability when the player's effect-keyword
/// state matches: present iff <see cref="EffectKeywordMustExist"/>; absent
/// iff <see cref="EffectKeywordMustNotExist"/>. <see cref="Default"/> marks
/// the fall-through entry to use when no other condition matches.
/// </summary>
public sealed class AbilityConditionalKeyword
{
    public string? Keyword { get; set; }
    public string? EffectKeywordMustExist { get; set; }
    public string? EffectKeywordMustNotExist { get; set; }
    public bool? Default { get; set; }
}
