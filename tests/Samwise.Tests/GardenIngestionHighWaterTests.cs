using System.IO;
using FluentAssertions;
using Mithril.Shared.Character;
using Mithril.Shared.Storage;
using Samwise.State;
using Xunit;

namespace Samwise.Tests;

/// <summary>
/// Restart-stability regression for #550 PR 3 archetype-B (Samwise L1 migration).
///
/// <para>Samwise is the textbook persisted-state-vs-replay collision case from
/// <a href="https://github.com/moumantai-gg/mithril/issues/549">#549</a>'s
/// disposition table: <see cref="GardenStateService.LoadAllAsync"/> hydrates plot
/// state from per-character <c>samwise.json</c>, then the L1 driver replays the
/// entire session on the LocalPlayer pipe. Without
/// <see cref="GardenCharacterState.HighWaterSequence"/> riding through to L1's
/// <c>SkipProcessedHighWater</c> filter, every plant /
/// <c>UpdateDescription</c> / <c>StartInteraction</c> / <c>GardeningXp</c>
/// event would re-apply on top of already-persisted plots, advancing stages
/// and burning slot caps that were already counted.</para>
///
/// <para>These tests cover the persistence + filter shape directly, without
/// spinning up the WPF dispatcher hop or the L1 driver itself. The L1
/// driver's <c>SkipProcessedHighWater</c> behaviour is pinned in
/// <c>tests/Mithril.Shared.Tests/Logging/LogStreamDriverTests</c> — here we
/// verify that Samwise's <em>persistence</em> end of the contract (load the
/// stored cursor, advance it under writes, write it back on Flush) survives
/// the round trip.</para>
/// </summary>
[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class GardenIngestionHighWaterTests : IDisposable
{
    private readonly string _root;
    private readonly string _charactersRoot;
    private readonly FakeActiveCharacterService _active;
    private readonly PerCharacterStore<GardenCharacterState> _store;
    private readonly GardenStateMachine _state;
    private readonly GardenStateService _service;

    public GardenIngestionHighWaterTests()
    {
        _root = Mithril.TestSupport.TestPaths.CreateTempDir("mithril-samwise-highwater");
        _charactersRoot = Path.Combine(_root, "characters");
        Directory.CreateDirectory(_charactersRoot);

        _active = new FakeActiveCharacterService();
        _active.Characters =
        [
            new CharacterSnapshot(
                Name: "Emraell",
                Server: "live",
                ExportedAt: DateTimeOffset.UtcNow,
                Skills: new Dictionary<string, CharacterSkill>(),
                RecipeCompletions: new Dictionary<string, int>(),
                NpcFavor: new Dictionary<string, string>()),
        ];
        _active.SetActiveCharacter("Emraell", "live");

        _store = new PerCharacterStore<GardenCharacterState>(
            _charactersRoot,
            "samwise.json",
            GardenCharacterStateJsonContext.Default.GardenCharacterState);

        _state = new GardenStateMachine(new InMemoryCropConfig(), activeChar: _active);
        _service = new GardenStateService(_state, _store, _active);
    }

    public void Dispose()
    {
        _service.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Default_high_water_is_zero_when_no_file_exists()
    {
        var (_, highWater) = await _service.LoadAllAsync();
        highWater.Should().Be(0L,
            "no persisted samwise.json yet — fresh-install shape must not filter any envelope");
    }

    [Fact]
    public void AdvanceHighWater_takes_max_and_never_regresses()
    {
        _service.AdvanceHighWater(100L);
        _service.CurrentHighWater.Should().Be(100L);

        _service.AdvanceHighWater(50L);
        _service.CurrentHighWater.Should().Be(100L,
            "out-of-order delivery cannot regress the cursor — max semantics");

        _service.AdvanceHighWater(200L);
        _service.CurrentHighWater.Should().Be(200L);
    }

    [Fact]
    public async Task Persisted_high_water_round_trips_through_samwise_json()
    {
        // Plant a plot so the character file gets created on Flush. The
        // OnChanged handler flags Emraell dirty; AdvanceHighWater additionally
        // touches every known character.
        var ts = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);
        _state.Apply(new Samwise.Parsing.SetPetOwner(ts, "12345"));
        _service.AdvanceHighWater(42L);

        // Wait for the debounce timer to fire.
        await FlushNowAsync();

        // Reload from disk through a fresh store/service to prove the value
        // crossed the JSON boundary (not just in-memory state).
        var (loaded, highWater) = await LoadFreshAsync();
        loaded.Should().HaveCount(1, "Emraell's samwise.json was created on Flush");
        highWater.Should().Be(42L, "the persisted high-water is fed back to L1 SkipProcessedHighWater");
    }

    [Fact]
    public async Task Restart_idempotence_byte_equivalence_under_high_water()
    {
        // Phase 1 — cold start: apply events 1..N and persist.
        var ts = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);
        _state.Apply(new Samwise.Parsing.SetPetOwner(ts, "111"));
        _service.AdvanceHighWater(1L);
        _state.Apply(new Samwise.Parsing.SetPetOwner(ts.AddSeconds(1), "222"));
        _service.AdvanceHighWater(2L);
        _state.Apply(new Samwise.Parsing.UpdateDescription(
            ts.AddSeconds(2), "111", "Onion", "thirsty plot", "Water Onion", 0.5));
        _service.AdvanceHighWater(3L);

        var snapshotBefore = SnapshotPlots();
        snapshotBefore.Should().ContainKey("Emraell/111");
        snapshotBefore.Should().ContainKey("Emraell/222");
        await FlushNowAsync();

        // Phase 2 — restart: hydrate from disk, then "replay" the same events.
        // Under the high-water filter, every event with Sequence <= 3 is
        // filtered out by L1 before reaching the handler. We model that here
        // by applying ONLY events whose Sequence > persistedHighWater.
        var (loaded, persistedHighWater) = await LoadFreshAsync();
        persistedHighWater.Should().Be(3L);

        var freshActive = new FakeActiveCharacterService();
        freshActive.Characters = _active.Characters;
        freshActive.SetActiveCharacter("Emraell", "live");
        var freshState = new GardenStateMachine(new InMemoryCropConfig(), activeChar: freshActive);
        foreach (var (charName, plots) in loaded)
            freshState.HydrateCharacter(charName, plots);

        // Simulate the L1 driver re-replaying Sequences 1..3 (all filtered
        // by SkipProcessedHighWater) AND adding nothing new — the post-
        // restart state must equal the pre-restart state byte-for-byte.
        // Any event with Sequence <= persistedHighWater is dropped before
        // the handler sees it; we deliberately don't call Apply for those.
        var snapshotAfter = SnapshotPlots(freshState);

        snapshotAfter.Should().BeEquivalentTo(snapshotBefore,
            "the high-water filter elides every replay event, so the rehydrated state " +
            "must be byte-identical to what we persisted");

        // And: feeding an envelope with Sequence > persistedHighWater (a
        // genuinely-new event from the live tail) does mutate state. The
        // filter is a high-water, not a stop-all.
        freshState.Apply(new Samwise.Parsing.SetPetOwner(ts.AddSeconds(10), "333"));
        var snapshotWithLive = SnapshotPlots(freshState);
        snapshotWithLive.Should().ContainKey("Emraell/333",
            "events with Sequence > persisted high-water still reach the handler");
    }

    // --- helpers --------------------------------------------------------------

    private static Task FlushNowAsync()
    {
        // The Flush path is debounced via a 500ms timer; in tests we wait
        // briefly to let it fire. Disposing the service also forces a final
        // Flush, but we keep the service alive for subsequent assertions so
        // we wait for the debounce instead. 750ms gives a 250ms slack over
        // the 500ms debounce on slow CI runners.
        return Task.Delay(750);
    }

    private async Task<(IReadOnlyList<(string CharName, IReadOnlyDictionary<string, PersistedPlot> Plots)> Characters, long HighWater)>
        LoadFreshAsync()
    {
        var freshActive = new FakeActiveCharacterService();
        freshActive.Characters = _active.Characters;
        freshActive.SetActiveCharacter("Emraell", "live");
        var freshStore = new PerCharacterStore<GardenCharacterState>(
            _charactersRoot,
            "samwise.json",
            GardenCharacterStateJsonContext.Default.GardenCharacterState);
        var freshState = new GardenStateMachine(new InMemoryCropConfig(), activeChar: freshActive);
        using var freshService = new GardenStateService(freshState, freshStore, freshActive);
        return await freshService.LoadAllAsync();
    }

    private Dictionary<string, (string? CropType, PlotStage Stage, string Title, string Action)>
        SnapshotPlots(GardenStateMachine? sm = null)
    {
        sm ??= _state;
        var result = new Dictionary<string, (string?, PlotStage, string, string)>(StringComparer.Ordinal);
        foreach (var (charName, plots) in sm.Snapshot())
            foreach (var (id, plot) in plots)
                result[$"{charName}/{id}"] = (plot.CropType, plot.Stage, plot.Title, plot.Action);
        return result;
    }
}
