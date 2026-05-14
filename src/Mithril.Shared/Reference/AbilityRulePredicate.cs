namespace Mithril.Shared.Reference;

/// <summary>
/// Predicate helpers shared by the three <c>abilitykeywords.json</c> /
/// <c>abilitydynamicdots.json</c> / <c>abilitydynamicspecialvalues.json</c>
/// rule shapes. Each rule row carries one or more <c>Req*Keywords</c> /
/// <c>MustHaveAbilityKeywords</c> string lists; a rule fires when every
/// required token is present in the candidate set (logical AND), with a
/// missing or empty required list treated as "no constraint" (always match).
/// Field-agnostic: the consumer (Effects tab, #244) decides which rule field
/// pairs with which candidate set.
/// </summary>
public static class AbilityRulePredicate
{
    /// <summary>
    /// Returns <c>true</c> when every entry in <paramref name="required"/> is also
    /// present in <paramref name="candidate"/> (ordinal comparison). A null/empty
    /// <paramref name="required"/> is treated as "no constraint" and matches anything.
    /// </summary>
    public static bool Matches(
        IReadOnlyList<string>? required,
        IReadOnlyList<string>? candidate)
    {
        if (required is null || required.Count == 0) return true;
        if (candidate is null || candidate.Count == 0) return false;
        for (var i = 0; i < required.Count; i++)
        {
            var token = required[i];
            var found = false;
            for (var j = 0; j < candidate.Count; j++)
            {
                if (string.Equals(candidate[j], token, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }
            if (!found) return false;
        }
        return true;
    }

    /// <summary>
    /// Single-string variant for <c>ReqActiveSkill</c> on <see cref="Mithril.Reference.Models.Misc.AbilityDynamicDot"/>.
    /// Null/empty <paramref name="required"/> means "no constraint".
    /// </summary>
    public static bool MatchesActiveSkill(string? required, string? candidate)
    {
        if (string.IsNullOrEmpty(required)) return true;
        return string.Equals(required, candidate, StringComparison.Ordinal);
    }
}
