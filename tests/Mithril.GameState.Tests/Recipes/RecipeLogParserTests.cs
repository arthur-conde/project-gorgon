using FluentAssertions;
using Mithril.GameState.Recipes.Parsing;
using Xunit;

namespace Mithril.GameState.Tests.Recipes;

public sealed class RecipeLogParserTests
{
    private readonly RecipeLogParser _parser = new();
    private static readonly DateTime Ts = new(2026, 5, 18, 10, 10, 3, DateTimeKind.Utc);

    // Real capture (trimmed to 5 representative rows incl. a high-count, a
    // mid-count, and two never-crafted count=0 rows) from Player.log's login
    // dump. Note PG's trailing comma in both arrays.
    private const string LoadRecipesLine =
        "[10:10:03] LocalPlayer: ProcessLoadRecipes(" +
        "[1,7025,8002,7026,13103,], [7,607,397,255,0,])";

    [Fact]
    public void ProcessLoadRecipes_yields_full_snapshot_in_log_order()
    {
        var evt = _parser.TryParse(LoadRecipesLine, Ts);

        var snap = evt.Should().BeOfType<RecipesSnapshotEvent>().Subject;
        snap.Timestamp.Should().Be(Ts);
        snap.Recipes.Select(r => r.RecipeId).Should().Equal(1, 7025, 8002, 7026, 13103);
    }

    [Fact]
    public void ProcessLoadRecipes_zips_ids_with_counts_1to1_absorbing_trailing_comma()
    {
        var snap = (RecipesSnapshotEvent)_parser.TryParse(LoadRecipesLine, Ts)!;

        snap.Recipes.Single(r => r.RecipeId == 7025).Completions.Should().Be(607);  // BoneMeal1
        snap.Recipes.Single(r => r.RecipeId == 8002).Completions.Should().Be(397);  // LeatherRoll2
        snap.Recipes.Single(r => r.RecipeId == 1).Completions.Should().Be(7);       // Butter
        snap.Recipes.Single(r => r.RecipeId == 13103).Completions.Should().Be(0);   // learned, never crafted
        snap.Recipes.Should().HaveCount(5);
    }

    [Theory]
    [InlineData("[10:22:39] LocalPlayer: ProcessUpdateRecipe(7026, 256)", 7026, 256)] // real craft
    [InlineData("[13:30:36] LocalPlayer: ProcessUpdateRecipe(13103, 1)", 13103, 1)]   // real first craft
    [InlineData("[13:32:20] LocalPlayer: ProcessUpdateRecipe(13104, 0)", 13104, 0)]   // real trainer learn
    public void ProcessUpdateRecipe_yields_single_record(string line, int id, int count)
    {
        var upd = _parser.TryParse(line, Ts).Should().BeOfType<RecipeUpdateEvent>().Subject;
        upd.Timestamp.Should().Be(Ts);
        upd.Recipe.RecipeId.Should().Be(id);
        upd.Recipe.Completions.Should().Be(count);
    }

    [Fact]
    public void ProcessLoadRecipes_tolerates_no_space_between_arrays()
    {
        var snap = _parser.TryParse(
            "LocalPlayer: ProcessLoadRecipes([1,2],[3,4])", Ts)
            .Should().BeOfType<RecipesSnapshotEvent>().Subject;
        snap.Recipes.Should().BeEquivalentTo(new[]
        {
            new RecipeCompletionRecord(1, 3),
            new RecipeCompletionRecord(2, 4),
        });
    }

    [Fact]
    public void Mismatched_array_lengths_returns_null_rather_than_corrupt_snapshot()
    {
        // Degenerate (truncation / grammar drift): a lopsided snapshot would
        // mis-pair ids with counts — emit nothing, let state stand.
        _parser.TryParse("LocalPlayer: ProcessLoadRecipes([1,2,3], [9,9])", Ts)
            .Should().BeNull();
    }

    [Fact]
    public void Degenerate_ProcessLoadRecipes_with_empty_arrays_returns_null()
    {
        // Emit nothing rather than an empty snapshot that would wipe live state.
        _parser.TryParse("[10:10:03] LocalPlayer: ProcessLoadRecipes([], [])", Ts)
            .Should().BeNull();
        _parser.TryParse("[10:10:03] LocalPlayer: ProcessLoadRecipes()", Ts)
            .Should().BeNull();
    }

    [Theory]
    [InlineData("[10:46:47] LocalPlayer: ProcessSetActiveSkills(Riding, Riding)")]
    [InlineData("[10:46:47] entity_23984278: OnAttackHitMe(Big Cat Claw). Evaded = False")]
    [InlineData("[13:32:20] LocalPlayer: ProcessTrainingScreenRemoveId(9376, 16)")]
    [InlineData("[11:54:19] LocalPlayer: ProcessShowRecipes(Teleportation)")]
    [InlineData("[10:10:03] LocalPlayer: ProcessSetStarredRecipes(System.Collections.Generic.HashSet`1[System.Int32])")]
    public void Unrelated_lines_return_null(string line)
        => _parser.TryParse(line, Ts).Should().BeNull();

    // ----- #525: per-row int.TryParse guard (containment vs. snapshot loss) -----

    [Fact]
    public void ProcessLoadRecipes_skips_oversized_id_row_and_keeps_the_rest()
    {
        // The middle id overflows int (>= 2^31). Per #525 row-skip policy, the
        // good rows still ship; only the bad PAIR (id+count) drops together so
        // parallel-array alignment is preserved.
        var line = "[10:10:03] LocalPlayer: ProcessLoadRecipes(" +
                   "[1,99999999999999,3,], [7,42,9,])";
        var snap = _parser.TryParse(line, Ts).Should().BeOfType<RecipesSnapshotEvent>().Subject;

        snap.Recipes.Should().BeEquivalentTo(new[]
        {
            new RecipeCompletionRecord(1, 7),
            new RecipeCompletionRecord(3, 9),
        });
    }

    [Fact]
    public void ProcessLoadRecipes_skips_oversized_count_row_and_keeps_the_rest()
    {
        // Symmetric: the count side overflows. The (id, count) pair drops.
        var line = "[10:10:03] LocalPlayer: ProcessLoadRecipes(" +
                   "[1,2,3,], [7,99999999999999,9,])";
        var snap = _parser.TryParse(line, Ts).Should().BeOfType<RecipesSnapshotEvent>().Subject;

        snap.Recipes.Select(r => r.RecipeId).Should().Equal(1, 3);
    }

    [Fact]
    public void ProcessLoadRecipes_with_all_rows_degenerate_returns_null()
    {
        // No good rows survive — treat as the degenerate empty case (line 83):
        // emit nothing, let prior snapshot stand until next transition.
        var line = "LocalPlayer: ProcessLoadRecipes(" +
                   "[99999999999999,88888888888888,], [0,0,])";
        _parser.TryParse(line, Ts).Should().BeNull();
    }

    [Fact]
    public void ProcessUpdateRecipe_with_oversized_id_returns_null()
    {
        // Single-record event: a malformed id is unrecoverable for THIS
        // event. Drop it; snapshot state stands.
        var line = "[10:22:39] LocalPlayer: ProcessUpdateRecipe(99999999999999, 1)";
        _parser.TryParse(line, Ts).Should().BeNull();
    }

    [Fact]
    public void ProcessUpdateRecipe_with_oversized_count_returns_null()
    {
        var line = "[10:22:39] LocalPlayer: ProcessUpdateRecipe(7026, 99999999999999)";
        _parser.TryParse(line, Ts).Should().BeNull();
    }

    [Fact]
    public void Oversized_token_does_not_throw_OverflowException()
    {
        // The whole point of #525: a single bad token must not poison the
        // parser. (Containment in PlayerRecipeStateService catches throws too,
        // but a throw would still discard the entire snapshot.)
        var line = "LocalPlayer: ProcessLoadRecipes([1,99999999999999,3,], [7,42,9,])";
        Action act = () => _parser.TryParse(line, Ts);
        act.Should().NotThrow();
    }
}
