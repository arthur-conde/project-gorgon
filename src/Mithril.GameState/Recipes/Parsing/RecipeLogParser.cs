using System.Globalization;
using System.Text.RegularExpressions;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;

namespace Mithril.GameState.Recipes.Parsing;

/// <summary>
/// Parses Project Gorgon's two recipe-state log lines into typed events:
///
/// <list type="bullet">
///   <item><c>LocalPlayer: ProcessLoadRecipes([id,…], [count,…])</c> — the full
///   known-recipe dump emitted at login and on zone / session transitions: two
///   parallel, equal-length integer arrays (recipe ids ‖ lifetime completion
///   counts). Yields a <see cref="RecipesSnapshotEvent"/> carrying every
///   parsed row.</item>
///   <item><c>LocalPlayer: ProcessUpdateRecipe(id, count)</c> — a single
///   recipe's new <em>absolute</em> completion count (<c>0</c> for a just-learned
///   recipe, <c>&gt;=1</c> for a craft). Yields a
///   <see cref="RecipeUpdateEvent"/>.</item>
/// </list>
///
/// <para>The regexes are unanchored — the convention every other
/// <c>Player.log</c> parser uses — and the guards pin the <c>LocalPlayer:</c>
/// prefix so an unrelated line that merely mentions the verb cannot trigger a
/// parse. A cheap substring pre-check short-circuits the (overwhelmingly
/// common) unrelated line before any regex runs. The arrays' trailing comma
/// (PG emits <c>[1,2,3,]</c>) is absorbed by splitting with empty-entry
/// removal.</para>
///
/// <para>Mirrors <see cref="Mithril.GameState.Skills.Parsing.SkillLogParser"/>
/// (the #462 template). Catalogued in <c>log-patterns.json</c> as
/// <c>shared.RecipeLogParser.{LoadRecipesRx,UpdateRecipeRx,LoadRecipesPayloadRx}</c>
/// (the <c>shared</c> module prefix matches the other <c>Mithril.GameState</c>
/// parsers in the catalog).</para>
/// </summary>
public sealed partial class RecipeLogParser : ILogParser
{
    // Guard: a real ProcessLoadRecipes line (full known-recipe dump). Pins the
    // LocalPlayer: prefix so only the genuine emitter matches.
    [GeneratedRegex(@"LocalPlayer: ProcessLoadRecipes\(", RegexOptions.CultureInvariant)]
    private static partial Regex LoadRecipesRx();

    // Guard: a real ProcessUpdateRecipe line.
    [GeneratedRegex(@"LocalPlayer: ProcessUpdateRecipe\(", RegexOptions.CultureInvariant)]
    private static partial Regex UpdateRecipeRx();

    // ProcessLoadRecipes payload: the two bracketed integer lists. PG separates
    // them with ", " but tolerate any whitespace. Recipe ids and counts are
    // non-negative integers; the lists carry a trailing comma.
    [GeneratedRegex(
        @"ProcessLoadRecipes\(\[(?<ids>[\d,]*)\]\s*,\s*\[(?<counts>[\d,]*)\]\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex LoadRecipesPayloadRx();

    // ProcessUpdateRecipe(id, count) — both non-negative integers.
    [GeneratedRegex(
        @"ProcessUpdateRecipe\((?<id>\d+)\s*,\s*(?<count>\d+)\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex UpdateRecipePayloadRx();

    private static readonly char[] Comma = [','];

    private readonly ThrottledWarn _warn;

    /// <summary>
    /// Constructs the parser. Pass an <see cref="IDiagnosticsSink"/> to surface
    /// the rare malformed-row breadcrumb that <see cref="ParseRow"/> emits when
    /// a numeric token fails <c>int.TryParse</c> (oversized id / count via
    /// grammar drift — guards against issue #525). A <c>null</c> sink makes the
    /// warn a no-op; the throttle window suppresses a per-line flood from a
    /// pathologically corrupt payload.
    /// </summary>
    public RecipeLogParser(IDiagnosticsSink? diag = null)
    {
        _warn = new ThrottledWarn(diag, "GameState.Recipes");
    }

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        // Fast path: the vast majority of Player.log lines are neither verb.
        if (!line.Contains("ProcessLoadRecipes", StringComparison.Ordinal)
            && !line.Contains("ProcessUpdateRecipe", StringComparison.Ordinal))
        {
            return null;
        }

        if (LoadRecipesRx().IsMatch(line))
        {
            var m = LoadRecipesPayloadRx().Match(line);
            if (!m.Success) return null;

            var ids = Split(m.Groups["ids"].Value);
            var counts = Split(m.Groups["counts"].Value);

            // The two arrays are parallel and must be equal length. A
            // mismatched or empty payload is degenerate (truncation / grammar
            // drift) — emit nothing rather than a spurious / lopsided snapshot
            // that would wipe or corrupt live state.
            if (ids.Length == 0 || ids.Length != counts.Length) return null;

            var recipes = new List<RecipeCompletionRecord>(ids.Length);
            for (int i = 0; i < ids.Length; i++)
            {
                // Per-row row-skip on overflow / non-numeric: parallel-array
                // alignment is preserved when the bad (id, count) PAIR drops
                // together. A 999-of-1000 partial snapshot is better than zero
                // — the missing row recovers on the next ProcessLoadRecipes
                // (zone / session transition). See #525.
                if (TryParseRow(ids[i], counts[i], out var record))
                    recipes.Add(record);
            }

            // All rows were degenerate — fall through to the same "emit
            // nothing" stance as line 83 (empty / mismatched payload). The
            // already-published snapshot stands until the next transition.
            if (recipes.Count == 0) return null;

            return new RecipesSnapshotEvent(timestamp, recipes);
        }

        if (UpdateRecipeRx().IsMatch(line))
        {
            var m = UpdateRecipePayloadRx().Match(line);
            if (!m.Success) return null;

            // Whole-event is one row; if either field overflows, drop the
            // event entirely (with breadcrumb). Snapshot state stands.
            if (!TryParseRow(m.Groups["id"].Value, m.Groups["count"].Value, out var record))
                return null;
            return new RecipeUpdateEvent(timestamp, record);
        }

        return null;
    }

    private bool TryParseRow(string idText, string countText, out RecipeCompletionRecord record)
    {
        if (int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            && int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            record = new RecipeCompletionRecord(id, count);
            return true;
        }
        _warn.Warn($"Dropping recipe row with unparseable numeric token (id='{idText}', count='{countText}')");
        record = default;
        return false;
    }

    private static string[] Split(string list) =>
        list.Split(Comma, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
