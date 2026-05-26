using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Tracks the player's skill levels and known recipes.
/// Receives dispatches from <see cref="LoadSkillsHandler"/>,
/// <see cref="UpdateSkillHandler"/>, <see cref="LoadRecipesHandler"/>,
/// and <see cref="UpdateRecipeHandler"/>.
/// </summary>
internal sealed class Player : IPlayerState
{
    private readonly Dictionary<string, SkillEntry> _skills = new(StringComparer.Ordinal);
    private readonly Dictionary<int, RecipeEntry> _recipes = [];
    private readonly IDomainEventPublisher _bus;
    private readonly InternPool _skillPool;

    public IReadOnlyDictionary<string, SkillEntry> Skills => _skills;
    public IReadOnlyDictionary<int, RecipeEntry> Recipes => _recipes;

    public Player(IDomainEventPublisher bus, InternPool skillPool)
    {
        _bus = bus;
        _skillPool = skillPool;
    }

    /// <summary>
    /// Clear all state on character switch to prevent stale data persisting.
    /// </summary>
    internal void Reset()
    {
        _skills.Clear();
        _recipes.Clear();
    }

    /// <summary>
    /// Args format: <c>({type=X,raw=N,bonus=N,xp=N,tnl=N,max=N}, {type=Y,...}, ...)</c>
    /// </summary>
    internal void OnLoadSkills(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        _skills.Clear();

        var tok = new ArgTokenizer(args);
        tok.SkipOpen();

        while (tok.HasMore)
        {
            var braced = tok.NextBracedSpan();
            if (braced.IsEmpty)
                break;

            if (TryParseSkillTuple(braced, out var key, out var entry))
                _skills[key] = entry;
        }

        _bus.Publish(new SkillsLoaded(_skills.Count, metadata));
    }

    /// <summary>
    /// Args format: <c>({type=X,raw=N,bonus=N,xp=N,tnl=N,max=N}, True, 25, 0, 0)</c>
    /// </summary>
    internal void OnUpdateSkill(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        var tok = new ArgTokenizer(args);
        tok.SkipOpen();

        var braced = tok.NextBracedSpan();
        if (braced.IsEmpty)
            return;

        if (!TryParseSkillTuple(braced, out var key, out var entry))
            return;

        tok.NextBool(); // announce flag — not used
        var xpGained = tok.NextInt();
        // remaining args (0, 0) unused

        _skills[key] = entry;
        _bus.Publish(new SkillUpdated(key, entry.Raw, entry.Bonus, entry.Xp, entry.Tnl, entry.Max, xpGained, metadata));
    }

    /// <summary>
    /// Args format: <c>([1,1026,1027,...,], [7,607,255,...,])</c>
    /// Parallel arrays: recipe ids and lifetime completion counts.
    /// </summary>
    internal void OnLoadRecipes(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        _recipes.Clear();

        var tok = new ArgTokenizer(args);
        tok.SkipOpen();

        var idsSpan = tok.NextBracketedSpan();
        var countsSpan = tok.NextBracketedSpan();

        var ids = ParseIntList(idsSpan);
        var counts = ParseIntList(countsSpan);

        var limit = Math.Min(ids.Count, counts.Count);
        for (var i = 0; i < limit; i++)
            _recipes[ids[i]] = new RecipeEntry(ids[i], counts[i]);

        _bus.Publish(new RecipesLoaded(_recipes.Count, metadata));
    }

    /// <summary>
    /// Args format: <c>(21000, 2)</c> — recipe id, new absolute lifetime count.
    /// </summary>
    internal void OnUpdateRecipe(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        var tok = new ArgTokenizer(args);
        tok.SkipOpen();

        var recipeId = tok.NextInt();
        var count = tok.NextInt();

        _recipes[recipeId] = new RecipeEntry(recipeId, count);
        _bus.Publish(new RecipeUpdated(recipeId, count, metadata));
    }

    private bool TryParseSkillTuple(ReadOnlySpan<char> braced, out string key, out SkillEntry entry)
    {
        key = string.Empty;
        entry = default;

        // Format: type=Surveying,raw=44,bonus=3,xp=246,tnl=2000,max=50
        const string typePrefix = "type=";
        const string rawPrefix = "raw=";
        const string bonusPrefix = "bonus=";
        const string xpPrefix = "xp=";
        const string tnlPrefix = "tnl=";
        const string maxPrefix = "max=";

        var typeIdx = braced.IndexOf(typePrefix);
        if (typeIdx < 0) return false;
        var afterType = braced[(typeIdx + typePrefix.Length)..];
        var typeEnd = afterType.IndexOf(',');
        if (typeEnd < 0) return false;
        key = _skillPool.InternOrAllocate(afterType[..typeEnd]);

        if (!TryExtractInt(braced, rawPrefix, out var raw)) return false;
        if (!TryExtractInt(braced, bonusPrefix, out var bonus)) return false;
        if (!TryExtractInt(braced, xpPrefix, out var xp)) return false;
        if (!TryExtractInt(braced, tnlPrefix, out var tnl)) return false;
        if (!TryExtractInt(braced, maxPrefix, out var max)) return false;

        entry = new SkillEntry(raw, bonus, xp, tnl, max);
        return true;
    }

    private static bool TryExtractInt(ReadOnlySpan<char> span, string prefix, out int value)
    {
        value = 0;
        var idx = span.IndexOf(prefix);
        if (idx < 0) return false;
        var after = span[(idx + prefix.Length)..];
        var end = after.IndexOfAny(',', '}');
        var token = end >= 0 ? after[..end] : after;
        return int.TryParse(token, out value);
    }

    private static List<int> ParseIntList(ReadOnlySpan<char> span)
    {
        var result = new List<int>();
        while (span.Length > 0)
        {
            var comma = span.IndexOf(',');
            var token = comma >= 0 ? span[..comma] : span;
            token = token.Trim();
            if (token.Length > 0 && int.TryParse(token, out var val))
                result.Add(val);
            if (comma < 0) break;
            span = span[(comma + 1)..];
        }
        return result;
    }
}
