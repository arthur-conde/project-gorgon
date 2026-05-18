using System.Threading.Channels;
using FluentAssertions;
using Mithril.GameState.Recipes;
using Mithril.GameState.Recipes.Parsing;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.GameState.Tests.Recipes;

public sealed class PlayerRecipeStateServiceTests
{
    // Real trimmed login dump: Butter=7, BoneMeal1=607, recipe_7026=255,
    // CraftedClothPants2(13103)=0 (known, never crafted at session start).
    private const string LoadLine =
        "[10:10:03] LocalPlayer: ProcessLoadRecipes([1,7025,7026,13103,], [7,607,255,0,])";

    private static PlayerRecipeStateService NewService(ScriptedStream stream)
        => new(stream, new RecipeLogParser());

    [Fact]
    public void Cold_start_is_Empty_with_no_measurement()
    {
        var svc = NewService(new ScriptedStream());
        svc.Current.Should().BeSameAs(PlayerRecipeSnapshot.Empty);
        svc.Current.Source.Should().Be(RecipeStateSource.None);
        svc.Current.MeasuredAt.Should().BeNull();
        svc.Current.Recipes.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessLoadRecipes_populates_full_snapshot_with_known_and_crafted_flags()
    {
        var stream = new ScriptedStream(new RawLogLine(Ts(10, 10, 3), LoadLine));
        var svc = NewService(stream);
        await RunUntilDrainedAsync(svc, stream);

        var cur = svc.Current;
        cur.Source.Should().Be(RecipeStateSource.LiveLog);
        cur.MeasuredAt.Should().Be(Ts(10, 10, 3));
        cur.Recipes.Should().HaveCount(4);

        cur.TryGet(7025, out var boneMeal).Should().BeTrue();
        boneMeal.Completions.Should().Be(607);
        boneMeal.IsCrafted.Should().BeTrue();

        cur.IsKnown(13103).Should().BeTrue();             // present == known
        cur.TryGet(13103, out var pants).Should().BeTrue();
        pants.Completions.Should().Be(0);
        pants.IsCrafted.Should().BeFalse();               // learned, never crafted

        cur.IsKnown(999999).Should().BeFalse();
    }

    [Fact]
    public async Task ProcessLoadRecipes_is_a_wholesale_replace_not_a_merge()
    {
        var stream = new ScriptedStream(
            new RawLogLine(Ts(10, 0, 0), "LocalPlayer: ProcessLoadRecipes([1,2], [3,4])"),
            new RawLogLine(Ts(11, 0, 0), "LocalPlayer: ProcessLoadRecipes([9], [1])"));
        var svc = NewService(stream);
        await RunUntilDrainedAsync(svc, stream);

        svc.Current.Recipes.Keys.Should().Equal(9); // 1 & 2 gone
        svc.Current.MeasuredAt.Should().Be(Ts(11, 0, 0));
    }

    [Fact]
    public async Task ProcessUpdateRecipe_upserts_one_recipe_keeping_the_rest()
    {
        var stream = new ScriptedStream(
            new RawLogLine(Ts(10, 10, 3), LoadLine),
            new RawLogLine(Ts(10, 22, 39), "LocalPlayer: ProcessUpdateRecipe(7026, 256)"));
        var svc = NewService(stream);
        await RunUntilDrainedAsync(svc, stream);

        svc.Current.Recipes.Should().HaveCount(4); // others untouched
        svc.Current.TryGet(7026, out var r).Should().BeTrue();
        r.Completions.Should().Be(256);
        svc.Current.MeasuredAt.Should().Be(Ts(10, 22, 39));
    }

    [Fact]
    public async Task ProcessUpdateRecipe_before_any_snapshot_yields_partial_state()
    {
        var stream = new ScriptedStream(new RawLogLine(Ts(13, 32, 20),
            "LocalPlayer: ProcessUpdateRecipe(13104, 0)"));
        var svc = NewService(stream);
        await RunUntilDrainedAsync(svc, stream);

        svc.Current.Source.Should().Be(RecipeStateSource.LiveLog);
        svc.Current.Recipes.Keys.Should().Equal(13104);
    }

    [Fact]
    public async Task Learn_of_previously_unknown_recipe_emits_Learned_change()
    {
        var stream = new ScriptedStream(new RawLogLine(Ts(10, 10, 3), LoadLine));
        var svc = NewService(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);

        var changes = new List<RecipeChange>();
        using (svc.SubscribeChanges(changes.Add))
        {
            // Real trainer learn of CraftedClothPants2E (count 0, not in dump).
            stream.Push("LocalPlayer: ProcessUpdateRecipe(13104, 0)");
            await stream.WaitForDrainAsync(cts.Token);
        }

        changes.Should().ContainSingle();
        var c = changes[0];
        c.Kind.Should().Be(RecipeChangeKind.Learned);
        c.RecipeId.Should().Be(13104);
        c.Previous.Should().BeNull();
        c.Current.Completions.Should().Be(0);
        c.CompletionsGained.Should().Be(0);
        c.IsFirstCompletion.Should().BeFalse();
        svc.Current.IsKnown(13104).Should().BeTrue();

        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task First_craft_of_a_known_zero_count_recipe_is_Completed_and_IsFirstCompletion()
    {
        var stream = new ScriptedStream(new RawLogLine(Ts(10, 10, 3), LoadLine));
        var svc = NewService(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);

        var changes = new List<RecipeChange>();
        using (svc.SubscribeChanges(changes.Add))
        {
            // 13103 is known at count 0 in the dump; real first craft → 1.
            stream.Push("[13:30:36] LocalPlayer: ProcessUpdateRecipe(13103, 1)");
            await stream.WaitForDrainAsync(cts.Token);
        }

        changes.Should().ContainSingle();
        var c = changes[0];
        c.Kind.Should().Be(RecipeChangeKind.Completed);
        c.RecipeId.Should().Be(13103);
        c.Previous!.Value.Completions.Should().Be(0);
        c.Current.Completions.Should().Be(1);
        c.CompletionsGained.Should().Be(1);
        c.IsFirstCompletion.Should().BeTrue();

        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Subsequent_craft_is_Completed_with_increment_and_not_first()
    {
        var stream = new ScriptedStream(
            new RawLogLine(Ts(10, 10, 3), LoadLine),
            new RawLogLine(Ts(10, 22, 39), "LocalPlayer: ProcessUpdateRecipe(7026, 256)")); // 255→256
        var svc = NewService(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);

        var changes = new List<RecipeChange>();
        using (svc.SubscribeChanges(changes.Add))
        {
            stream.Push("LocalPlayer: ProcessUpdateRecipe(7026, 258)"); // +2
            await stream.WaitForDrainAsync(cts.Token);
        }

        changes.Should().ContainSingle();
        var c = changes[0];
        c.Kind.Should().Be(RecipeChangeKind.Completed);
        c.Previous!.Value.Completions.Should().Be(256);
        c.Current.Completions.Should().Be(258);
        c.CompletionsGained.Should().Be(2);
        c.IsFirstCompletion.Should().BeFalse(); // prior count was 256, not 0

        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Craft_with_no_baseline_is_Completed_but_not_flagged_first()
    {
        // Mid-session start: first thing seen for this recipe is a craft, no
        // prior snapshot. Honest contract — we don't claim "first ever".
        var stream = new ScriptedStream();
        var svc = NewService(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await svc.StartAsync(cts.Token);

        var changes = new List<RecipeChange>();
        using (svc.SubscribeChanges(changes.Add))
        {
            stream.Push("LocalPlayer: ProcessUpdateRecipe(7026, 256)");
            await stream.WaitForDrainAsync(cts.Token);
        }

        changes.Should().ContainSingle();
        var c = changes[0];
        c.Kind.Should().Be(RecipeChangeKind.Completed);
        c.Previous.Should().BeNull();
        c.CompletionsGained.Should().Be(0);    // no baseline → no attributed gain
        c.IsFirstCompletion.Should().BeFalse(); // no baseline → not flagged

        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Idempotent_re_emit_at_same_count_produces_no_change_and_no_snapshot_churn()
    {
        var stream = new ScriptedStream(new RawLogLine(Ts(10, 10, 3), LoadLine));
        var svc = NewService(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);

        var snaps = new List<PlayerRecipeSnapshot>();
        var changes = new List<RecipeChange>();
        using (svc.Subscribe(snaps.Add))
        using (svc.SubscribeChanges(changes.Add))
        {
            snaps.Should().ContainSingle(); // replay only
            // 7026 is already known at 255 in the dump; re-emit same value.
            stream.Push("LocalPlayer: ProcessUpdateRecipe(7026, 255)");
            await stream.WaitForDrainAsync(cts.Token);
        }

        changes.Should().BeEmpty();      // no movement → no change event
        snaps.Should().ContainSingle();  // no spurious snapshot push

        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task SnapshotReplace_emits_only_recipes_that_actually_changed()
    {
        var stream = new ScriptedStream(new RawLogLine(Ts(10, 0, 0),
            "LocalPlayer: ProcessLoadRecipes([1,7026], [7,255])"));
        var svc = NewService(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);

        var changes = new List<RecipeChange>();
        using (svc.SubscribeChanges(changes.Add))
        {
            // Re-sync: 1 identical (no-op), 7026 moved 255→256, 13103 new.
            stream.Push("LocalPlayer: ProcessLoadRecipes([1,7026,13103], [7,256,0])");
            await stream.WaitForDrainAsync(cts.Token);
        }

        changes.Should().HaveCount(2); // recipe 1 unchanged → suppressed
        changes.Should().AllSatisfy(c => c.Kind.Should().Be(RecipeChangeKind.SnapshotReplace));
        changes.Should().AllSatisfy(c => c.CompletionsGained.Should().Be(0)); // snapshot not a gain

        var moved = changes.Single(c => c.RecipeId == 7026);
        moved.Previous!.Value.Completions.Should().Be(255);
        moved.Current.Completions.Should().Be(256);

        var added = changes.Single(c => c.RecipeId == 13103);
        added.Previous.Should().BeNull();

        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Subscribe_replays_current_then_delivers_live_changes()
    {
        var stream = new ScriptedStream(new RawLogLine(Ts(10, 10, 3), LoadLine));
        var svc = NewService(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);

        var seen = new List<PlayerRecipeSnapshot>();
        using (svc.Subscribe(seen.Add))
        {
            seen.Should().HaveCount(1); // replay of current
            seen[0].Recipes.Should().HaveCount(4);

            stream.Push("LocalPlayer: ProcessUpdateRecipe(13104, 0)");
            await stream.WaitForDrainAsync(cts.Token);
        }

        seen.Should().HaveCount(2);
        seen[1].IsKnown(13104).Should().BeTrue();

        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task SubscribeChanges_has_no_replay()
    {
        var stream = new ScriptedStream(new RawLogLine(Ts(10, 10, 3), LoadLine));
        var svc = NewService(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);

        var changes = new List<RecipeChange>();
        using (svc.SubscribeChanges(changes.Add))
        {
            changes.Should().BeEmpty(); // a change is an event, not state
            stream.Push("LocalPlayer: ProcessUpdateRecipe(7026, 256)");
            await stream.WaitForDrainAsync(cts.Token);
        }

        changes.Should().ContainSingle();

        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Disposed_subscription_stops_receiving()
    {
        var stream = new ScriptedStream(new RawLogLine(Ts(10, 10, 3), LoadLine));
        var svc = NewService(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);

        var seen = new List<PlayerRecipeSnapshot>();
        var sub = svc.Subscribe(seen.Add);
        sub.Dispose();

        stream.Push("LocalPlayer: ProcessUpdateRecipe(13104, 0)");
        await stream.WaitForDrainAsync(cts.Token);

        seen.Should().HaveCount(1); // only the replay; nothing after dispose

        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
    }

    private static DateTime Ts(int h, int m, int s) => new(2026, 5, 18, h, m, s, DateTimeKind.Utc);

    private static async Task RunUntilDrainedAsync(PlayerRecipeStateService svc, ScriptedStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);
        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
    }

    private sealed class ScriptedStream : IPlayerLogStream
    {
        private readonly Channel<RawLogLine> _channel = Channel.CreateUnbounded<RawLogLine>();
        private long _pending;
        private TaskCompletionSource _drained = NewDrainTcs();

        public ScriptedStream(params RawLogLine[] lines)
        {
            if (lines.Length == 0)
            {
                _drained.TrySetResult();
                return;
            }
            Interlocked.Add(ref _pending, lines.Length);
            foreach (var line in lines) _channel.Writer.TryWrite(line);
        }

        public void Push(string line)
        {
            Interlocked.Increment(ref _pending);
            Interlocked.Exchange(ref _drained, NewDrainTcs());
            _channel.Writer.TryWrite(new RawLogLine(DateTime.UtcNow, line));
        }

        public Task WaitForDrainAsync(CancellationToken ct) => _drained.Task.WaitAsync(ct);

        public async IAsyncEnumerable<RawLogLine> SubscribeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var line))
                {
                    yield return line;
                    if (Interlocked.Decrement(ref _pending) == 0)
                        _drained.TrySetResult();
                }
            }
        }

        private static TaskCompletionSource NewDrainTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
