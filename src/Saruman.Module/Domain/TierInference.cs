namespace Saruman.Domain;

public static class TierInference
{
    /// <summary>
    /// Infer a tier (1–6) from a WoP code by counting syllable nuclei.
    /// The game uses recipes named after syllable counts: tier 1 = 2-syllable,
    /// tier 2 = 3-syllable, … tier 6 = 7-syllable. Raw character length is
    /// unreliable (TEVKUM and BWUBGUCH are both tier 1 but differ by 2 chars),
    /// so we count groups of consecutive vowels — Y counts as a vowel when
    /// flanked by consonants. Still imperfect but much closer than length.
    /// The authoritative signal is the neighbouring ProcessUpdateRecipe event;
    /// TODO: correlate that to drop this heuristic entirely.
    /// </summary>
    public static WordOfPowerTier FromCode(string code)
    {
        var syllables = CountSyllables(code);
        // Syllables 2→tier 1, 3→tier 2, …, 7+→tier 6.
        var t = syllables <= 2 ? 1 : Math.Min(syllables - 1, 6);
        return (WordOfPowerTier)t;
    }

    private static int CountSyllables(string? code)
    {
        if (string.IsNullOrEmpty(code)) return 0;

        var groups = 0;
        var inVowel = false;
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            var isVowel = c is 'A' or 'E' or 'I' or 'O' or 'U'
                       || (c == 'Y' && IsYVowel(code, i));
            if (isVowel && !inVowel) groups++;
            inVowel = isVowel;
        }
        return groups;
    }

    // Y is a vowel when it isn't word-initial and the previous letter is a
    // consonant (CRY, STYUSLARR). Word-initial Y in a syllable onset (YES-) is
    // rare in these codes so treating all non-initial Y as vowel is safe.
    private static bool IsYVowel(string code, int i) => i > 0 && !IsVowelLetter(code[i - 1]);

    private static bool IsVowelLetter(char c) => c is 'A' or 'E' or 'I' or 'O' or 'U';
}
