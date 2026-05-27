using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Tracks the player's skill levels and known recipes. Handles its own
/// verbs directly (adapter collapse): LoadSkills, UpdateSkill,
/// LoadRecipes, UpdateRecipe. Exposes both the enriched
/// <see cref="ISkillState"/> (copy-on-write snapshots) and the legacy
/// <see cref="IPlayerState"/> (raw entries).
/// </summary>
internal sealed class Player : IPlayerState, ISkillState
{
    private readonly Dictionary<string, SkillEntry> _rawSkills = new(StringComparer.Ordinal);
    private readonly Dictionary<int, RecipeEntry> _recipes = [];
    private readonly IDomainEventPublisher _bus;
    private readonly InternPool _skillPool;

    private Dictionary<string, SkillSnapshot> _skillSnapshots = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, SkillEntry> Skills => _rawSkills;
    public IReadOnlyDictionary<int, RecipeEntry> Recipes => _recipes;

    IReadOnlyDictionary<string, SkillSnapshot> ISkillState.Skills => _skillSnapshots;

    public Player(IDomainEventPublisher bus, InternPool skillPool)
    {
        _bus = bus;
        _skillPool = skillPool;
    }

    internal IFrameHandler LoadSkillsHandler => new LoadSkillsVerb(this);
    internal IFrameHandler UpdateSkillHandler => new UpdateSkillVerb(this);
    internal IFrameHandler LoadRecipesHandler => new LoadRecipesVerb(this);
    internal IFrameHandler UpdateRecipeHandler => new UpdateRecipeVerb(this);

    internal void Reset()
    {
        _rawSkills.Clear();
        _recipes.Clear();
        _skillSnapshots = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Args format: <c>({type=X,raw=N,bonus=N,xp=N,tnl=N,max=N}, {type=Y,...}, ...)</c>
    /// </summary>
    private void OnLoadSkills(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
    {
        _rawSkills.Clear();

        var tok = new ArgTokenizer(args, verb, sourceLog);
        tok.SkipOpen();

        while (tok.HasMore)
        {
            var braced = tok.NextBracedSpan();
            if (braced.IsEmpty)
                break;

            if (TryParseSkillTuple(braced, out var key, out var entry))
                _rawSkills[key] = entry;
        }

        var measuredAt = metadata.Timestamp ?? metadata.ReadOn;
        RebuildSkillSnapshots(measuredAt);
        _bus.Publish(new SkillsLoaded(_rawSkills.Count, metadata));
    }

    /// <summary>
    /// Args format: <c>({type=X,raw=N,bonus=N,xp=N,tnl=N,max=N}, True, 25, 0, 0)</c>
    /// </summary>
    private void OnUpdateSkill(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
    {
        var tok = new ArgTokenizer(args, verb, sourceLog);
        tok.SkipOpen();

        var braced = tok.NextBracedSpan();
        if (braced.IsEmpty)
            return;

        if (!TryParseSkillTuple(braced, out var key, out var entry))
            return;

        tok.NextBool(); // announce flag — not used
        var xpGained = tok.NextInt();

        _rawSkills[key] = entry;

        var measuredAt = metadata.Timestamp ?? metadata.ReadOn;
        var snapshot = new SkillSnapshot(entry.Raw, entry.Bonus, entry.Xp, entry.Tnl, entry.Max, measuredAt);
        var newDict = new Dictionary<string, SkillSnapshot>(_skillSnapshots, StringComparer.Ordinal)
        {
            [key] = snapshot
        };
        _skillSnapshots = newDict;

        _bus.Publish(new SkillUpdated(key, entry.Raw, entry.Bonus, entry.Xp, entry.Tnl, entry.Max, xpGained, metadata));
    }

    /// <summary>
    /// Args format: <c>([1,1026,1027,...,], [7,607,255,...,])</c>
    /// </summary>
    private void OnLoadRecipes(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
    {
        _recipes.Clear();

        var tok = new ArgTokenizer(args, verb, sourceLog);
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
    /// Args format: <c>(21000, 2)</c>
    /// </summary>
    private void OnUpdateRecipe(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
    {
        var tok = new ArgTokenizer(args, verb, sourceLog);
        tok.SkipOpen();

        var recipeId = tok.NextInt();
        var count = tok.NextInt();

        _recipes[recipeId] = new RecipeEntry(recipeId, count);
        _bus.Publish(new RecipeUpdated(recipeId, count, metadata));
    }

    private void RebuildSkillSnapshots(DateTimeOffset measuredAt)
    {
        var dict = new Dictionary<string, SkillSnapshot>(_rawSkills.Count, StringComparer.Ordinal);
        foreach (var (key, e) in _rawSkills)
            dict[key] = new SkillSnapshot(e.Raw, e.Bonus, e.Xp, e.Tnl, e.Max, measuredAt);
        _skillSnapshots = dict;
    }

    private bool TryParseSkillTuple(ReadOnlySpan<char> braced, out string key, out SkillEntry entry)
    {
        key = string.Empty;
        entry = default;

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

    private sealed class LoadSkillsVerb(Player owner) : IFrameHandler
    {
        public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
            => owner.OnLoadSkills(args, verb, sourceLog, metadata);
    }

    private sealed class UpdateSkillVerb(Player owner) : IFrameHandler
    {
        public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
            => owner.OnUpdateSkill(args, verb, sourceLog, metadata);
    }

    private sealed class LoadRecipesVerb(Player owner) : IFrameHandler
    {
        public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
            => owner.OnLoadRecipes(args, verb, sourceLog, metadata);
    }

    private sealed class UpdateRecipeVerb(Player owner) : IFrameHandler
    {
        public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
            => owner.OnUpdateRecipe(args, verb, sourceLog, metadata);
    }
}
