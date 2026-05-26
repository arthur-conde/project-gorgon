using System.IO;
using Arda.Abstractions.Logs;
using Arda.World.Player.Events;
using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Mithril.Shared.Character;
using Mithril.Shared.Settings;
using Xunit;

namespace Gandalf.Tests;

[Trait("Category", "FileIO")]
[Collection("FileIO")]
public class LootBracketTrackerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _charactersDir;
    private readonly string _cachePath;

    public LootBracketTrackerTests()
    {
        _dir = Mithril.TestSupport.TestPaths.CreateTempDir("gandalf_bracket_tracker");
        _charactersDir = Path.Combine(_dir, "characters");
        _cachePath = Path.Combine(_dir, "loot-catalog.json");
        Directory.CreateDirectory(_charactersDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private (LootSource src, LootBracketTracker tracker, DerivedTimerProgressService derived)
        Build()
    {
        var active = new FakeActiveCharacterService();
        active.SetActiveCharacter("Arthur", "Kwatoxi");
        var time = new ManualTime(new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc));

        var derivedStore = new PerCharacterStore<DerivedProgress>(_charactersDir, "gandalf-derived.json",
            DerivedProgressJsonContext.Default.DerivedProgress);
        var derivedView = new PerCharacterView<DerivedProgress>(active, derivedStore);
        var derived = new DerivedTimerProgressService(derivedView, time);

        var cacheStore = new JsonSettingsStore<LootCatalogCache>(_cachePath,
            LootCatalogCacheJsonContext.Default.LootCatalogCache);
        var cache = cacheStore.Load();
        var src = new LootSource(derived, cacheStore, cache, time: time);
        var tracker = new LootBracketTracker(src);

        return (src, tracker, derived);
    }

    private static readonly DateTime EventTime = new(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);

    // ── Event factory helpers ──────────────────────────────────────────

    private static LogLineMetadata Meta(DateTime ts, bool isReplay = false) =>
        new(new DateTimeOffset(ts, TimeSpan.Zero), DateTimeOffset.UtcNow, isReplay);

    private static InteractionStarted Start(long entityId, string name, DateTime ts) =>
        new(entityId, name, 0, false, Meta(ts));

    private static EnableInteractorsFrame Enable(long interactorId, DateTime ts) =>
        new(interactorId, Meta(ts));

    private static InteractionEnded End(long entityId, DateTime ts) =>
        new(entityId, Meta(ts));

    private static ScreenTextObserved ScreenText(string category, string text, DateTime ts) =>
        new(category.AsMemory(), text.AsMemory(), Meta(ts));

    private static DelayLoopStarted Delay(double seconds, string verb, string text, bool isInteractor, DateTime ts) =>
        new(seconds, verb.AsMemory(), text.AsMemory(), isInteractor, Meta(ts));

    private static InteractionWaiting Wait(long entityId, string body, DateTime ts) =>
        new(entityId, body.AsMemory(), Meta(ts));

    // ── Tests ──────────────────────────────────────────────────────────

    /// <summary>
    /// Live capture: EltibuleSecretChest bracket. The signal-driven tracker
    /// correctly identifies it as loot via the AddItem inside the bracket.
    /// </summary>
    [Fact]
    public void Loot_chest_with_AddItem_creates_progress_row_and_caches_duration()
    {
        var (src, tracker, derived) = Build();
        try
        {
            src.OnChestCooldownObserved("EltibuleSecretChest", TimeSpan.FromHours(3));

            tracker.OnInteractionStarted(Start(-147, "EltibuleSecretChest", EventTime));
            tracker.OnInventoryItemAdded(EventTime);
            tracker.OnInventoryItemAdded(EventTime);
            tracker.OnEnableInteractors(Enable(-147, EventTime));

            src.Progress.Should().ContainKey(LootSource.ChestKey("EltibuleSecretChest"));
            tracker.IsInFlight.Should().BeFalse();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// Storage Box ("StorageCatalog") — TalkScreen discards the bracket.
    /// </summary>
    [Fact]
    public void StorageCatalog_with_TalkScreen_creates_no_row()
    {
        var (src, tracker, derived) = Build();
        try
        {
            tracker.OnInteractionStarted(Start(-28, "StorageCatalog", EventTime));
            tracker.OnTalkScreen();
            tracker.IsInFlight.Should().BeFalse();

            src.Progress.Should().BeEmpty();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// Workstations emit a delay loop with <c>IsInteractorDelayLoop</c>
    /// when the player crafts — that stashes a harvest verb which suppresses
    /// the chest commit on the subsequent AddItem.
    /// </summary>
    [Theory]
    [InlineData("Fireplace")]
    [InlineData("TanningRack")]
    [InlineData("TeleportationPlatform")]
    public void Workstation_with_interactor_delay_loop_creates_no_row(string entity)
    {
        var (src, tracker, derived) = Build();
        try
        {
            tracker.OnInteractionStarted(Start(-16, entity, EventTime));
            tracker.OnDelayLoopStarted(Delay(3, "Craft", $"Using {entity}...", true, EventTime));
            tracker.OnInventoryItemAdded(EventTime);

            src.Progress.Should().BeEmpty();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// <c>InteractionWaiting</c> with a non-empty body is the harvest
    /// signal for activities like filling water bottles at a well.
    /// </summary>
    [Fact]
    public void Wait_interaction_with_body_suppresses_chest_commit()
    {
        var (src, tracker, derived) = Build();
        try
        {
            tracker.OnInteractionStarted(Start(-2, "WaterWell", EventTime));
            tracker.OnInteractionWaiting(Wait(-2, "Empty Bottles: 8 Bottles of Water: 0", EventTime));
            tracker.OnInventoryItemAdded(EventTime);

            src.Progress.Should().BeEmpty();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// Empty-body <c>InteractionWaiting</c> (the IvynsChest unlock
    /// animation) is NOT a harvest signal. A subsequent real chest
    /// interaction that replaces the bracket still commits.
    /// </summary>
    [Fact]
    public void Empty_body_wait_interaction_does_not_suppress_subsequent_real_chest()
    {
        var (src, tracker, derived) = Build();
        try
        {
            src.OnChestCooldownObserved("EltibuleSecretChest", TimeSpan.FromHours(3));

            // First bracket (e.g. storage chest) — stays open until replaced.
            tracker.OnInteractionStarted(Start(-45, "IvynsChest", EventTime));
            tracker.OnInteractionWaiting(Wait(-45, "", EventTime));

            // Real chest interaction replaces the stale bracket.
            tracker.OnInteractionStarted(Start(-147, "EltibuleSecretChest", EventTime));
            tracker.OnInventoryItemAdded(EventTime);

            src.Progress.Should().ContainKey(LootSource.ChestKey("EltibuleSecretChest"));
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// Stray <c>InteractionWaiting</c> from a different interactor id
    /// must not stash a harvest verb on the in-flight bracket.
    /// </summary>
    [Fact]
    public void Wait_interaction_with_non_matching_id_does_not_poison_bracket()
    {
        var (src, tracker, derived) = Build();
        try
        {
            src.OnChestCooldownObserved("EltibuleSecretChest", TimeSpan.FromHours(3));

            tracker.OnInteractionStarted(Start(-147, "EltibuleSecretChest", EventTime));
            tracker.OnInteractionWaiting(Wait(-999, "Filling Water Bottles...", EventTime));
            tracker.OnInventoryItemAdded(EventTime);

            src.Progress.Should().ContainKey(LootSource.ChestKey("EltibuleSecretChest"));
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// Soft timeout backstop: a bracket older than <see cref="LootBracketTracker.SoftTimeout"/>
    /// with no positive signal must not commit a subsequent AddItem.
    /// </summary>
    [Fact]
    public void Bracket_older_than_soft_timeout_does_not_commit_subsequent_AddItem()
    {
        var (src, tracker, derived) = Build();
        try
        {
            tracker.OnInteractionStarted(Start(16189159, "SummonedHorseApple", EventTime));

            var late = EventTime + TimeSpan.FromSeconds(5);
            tracker.OnInventoryItemAdded(late);

            src.Progress.Should().BeEmpty();
            tracker.IsInFlight.Should().BeFalse();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// Cow milking on cooldown: the rejection rides on the ErrorMessage
    /// channel of <see cref="ScreenTextObserved"/>.
    /// </summary>
    [Fact]
    public void Cow_milking_rejection_caches_one_hour_duration()
    {
        var (src, tracker, derived) = Build();
        try
        {
            tracker.OnInteractionStarted(Start(5298, "Cow_Bessie", EventTime));
            tracker.OnScreenTextObserved(ScreenText("ErrorMessage",
                "You've already milked Bessie in the past hour.", EventTime));

            tracker.IsInFlight.Should().BeFalse();

            var catalogEntry = src.Catalog.Should().ContainSingle(c => c.DisplayName == "Cow_Bessie").Subject;
            catalogEntry.Duration.Should().Be(TimeSpan.FromHours(1));
            catalogEntry.SourceMetadata.Should().BeOfType<LootCatalogPayload>()
                .Which.IsDurationVerified.Should().BeTrue();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// Numeric form of the cow rejection.
    /// </summary>
    [Theory]
    [InlineData("You've already milked Bessie in the past 30 minutes.", 30, "minutes", "Cow_Bessie")]
    [InlineData("You've already milked Moolanda in the past 5 minutes.", 5, "minutes", "Cow_Moolanda")]
    [InlineData("You've already milked Bessie in the past 2 hours.", 2, "hours", "Cow_Bessie")]
    public void Cow_milking_rejection_numeric_form_caches_correct_duration(
        string body, int value, string unit, string entityName)
    {
        var (src, tracker, derived) = Build();
        try
        {
            tracker.OnInteractionStarted(Start(5298, entityName, EventTime));
            tracker.OnScreenTextObserved(ScreenText("ErrorMessage", body, EventTime));

            var expected = unit.StartsWith("minute") ? TimeSpan.FromMinutes(value) : TimeSpan.FromHours(value);
            var catalogEntry = src.Catalog.Should().ContainSingle(c => c.DisplayName == entityName).Subject;
            catalogEntry.Duration.Should().Be(expected);
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// First milking → placeholder row. Second milking inside cooldown →
    /// rejection upgrades duration, verified flips to true, anchor preserved.
    /// </summary>
    [Fact]
    public void Cow_milk_then_rejection_upgrades_unverified_placeholder()
    {
        var (src, tracker, derived) = Build();
        try
        {
            tracker.OnInteractionStarted(Start(5298, "Cow_Bessie", EventTime));
            tracker.OnInventoryItemAdded(EventTime);

            var initialEntry = src.Catalog.Should().ContainSingle(c => c.DisplayName == "Cow_Bessie").Subject;
            initialEntry.Duration.Should().Be(LootSource.PlaceholderChestDuration);
            initialEntry.SourceMetadata.Should().BeOfType<LootCatalogPayload>()
                .Which.IsDurationVerified.Should().BeFalse();

            var firstMilkAt = src.Progress[LootSource.ChestKey("Cow_Bessie")].StartedAt;

            var laterTime = EventTime + TimeSpan.FromMinutes(10);
            tracker.OnInteractionStarted(Start(5298, "Cow_Bessie", laterTime));
            tracker.OnScreenTextObserved(ScreenText("ErrorMessage",
                "You've already milked Bessie in the past hour.", laterTime));

            var upgradedEntry = src.Catalog.Should().ContainSingle(c => c.DisplayName == "Cow_Bessie").Subject;
            upgradedEntry.Duration.Should().Be(TimeSpan.FromHours(1));
            upgradedEntry.SourceMetadata.Should().BeOfType<LootCatalogPayload>()
                .Which.IsDurationVerified.Should().BeTrue();
            src.Progress[LootSource.ChestKey("Cow_Bessie")].StartedAt.Should().Be(firstMilkAt);
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// A milking-rejection grammar inside a non-cow bracket must NOT apply
    /// the cow duration. Name-prefix gating guards against poisoning.
    /// </summary>
    [Fact]
    public void Milking_rejection_inside_non_cow_bracket_does_not_apply_duration()
    {
        var (src, tracker, derived) = Build();
        try
        {
            src.OnChestCooldownObserved("EltibuleSecretChest", TimeSpan.FromHours(3));

            tracker.OnInteractionStarted(Start(-147, "EltibuleSecretChest", EventTime));
            tracker.OnScreenTextObserved(ScreenText("ErrorMessage",
                "You've already milked Bessie in the past hour.", EventTime));

            tracker.IsInFlight.Should().BeTrue();

            var chestEntry = src.Catalog.Should().ContainSingle(c => c.DisplayName == "EltibuleSecretChest").Subject;
            chestEntry.Duration.Should().Be(TimeSpan.FromHours(3));
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// Soft-timeout boundary check: AddItem at exactly the timeout still commits.
    /// </summary>
    [Fact]
    public void AddItem_at_soft_timeout_boundary_still_commits()
    {
        var (src, tracker, derived) = Build();
        try
        {
            src.OnChestCooldownObserved("EltibuleSecretChest", TimeSpan.FromHours(3));

            tracker.OnInteractionStarted(Start(-147, "EltibuleSecretChest", EventTime));
            var atBoundary = EventTime + LootBracketTracker.SoftTimeout;
            tracker.OnInventoryItemAdded(atBoundary);

            src.Progress.Should().ContainKey(LootSource.ChestKey("EltibuleSecretChest"));
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// NPC dialog interaction — TalkScreen discards the bracket.
    /// </summary>
    [Fact]
    public void NPC_dialog_creates_no_row()
    {
        var (src, tracker, derived) = Build();
        try
        {
            src.OnChestCooldownObserved("NpcMaxine", TimeSpan.FromHours(1));

            tracker.OnInteractionStarted(Start(42, "NpcMaxine", EventTime));
            tracker.OnTalkScreen();

            src.Progress.Should().BeEmpty();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// Cooldown rejection inside the bracket — caches the duration without
    /// creating a progress row.
    /// </summary>
    [Fact]
    public void Rejection_inside_bracket_caches_duration_without_creating_row()
    {
        var (src, tracker, derived) = Build();
        try
        {
            tracker.OnInteractionStarted(Start(-162, "GoblinStaticChest1", EventTime));
            tracker.OnScreenTextObserved(ScreenText("GeneralInfo",
                "You've already looted this chest! (It will refill 3 hours after you looted it.)", EventTime));

            src.Catalog.Should().Contain(c => c.DisplayName == "GoblinStaticChest1");
            src.Progress.Should().BeEmpty();
            tracker.IsInFlight.Should().BeFalse();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// AddItem outside any bracket creates no row.
    /// </summary>
    [Fact]
    public void AddItem_outside_bracket_creates_no_row()
    {
        var (src, tracker, derived) = Build();
        try
        {
            src.OnChestCooldownObserved("GoblinStaticChest1", TimeSpan.FromHours(3));

            // Bare AddItem with no preceding StartInteraction.
            tracker.OnInventoryItemAdded(EventTime);
            // TalkScreen-discarded bracket then AddItem (e.g., quest reward).
            tracker.OnInteractionStarted(Start(42, "NpcQuestGiver", EventTime));
            tracker.OnTalkScreen();
            tracker.OnInventoryItemAdded(EventTime);

            src.Progress.Should().BeEmpty();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// New StartInteraction replaces an in-flight bracket without committing.
    /// </summary>
    [Fact]
    public void New_Start_replaces_in_flight_bracket()
    {
        var (src, tracker, derived) = Build();
        try
        {
            src.OnChestCooldownObserved("ChestA", TimeSpan.FromHours(1));
            src.OnChestCooldownObserved("ChestB", TimeSpan.FromHours(2));

            tracker.OnInteractionStarted(Start(-100, "ChestA", EventTime));
            tracker.OnInteractionStarted(Start(-101, "ChestB", EventTime));
            tracker.OnInventoryItemAdded(EventTime);

            src.Progress.Should().ContainKey(LootSource.ChestKey("ChestB"));
            src.Progress.Should().NotContainKey(LootSource.ChestKey("ChestA"));
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// Multiple AddItems in one bracket only commit once.
    /// </summary>
    [Fact]
    public void Multiple_AddItems_in_one_bracket_only_commit_once()
    {
        var (src, tracker, derived) = Build();
        try
        {
            src.OnChestCooldownObserved("EltibuleSecretChest", TimeSpan.FromHours(3));

            tracker.OnInteractionStarted(Start(-147, "EltibuleSecretChest", EventTime));
            tracker.OnInventoryItemAdded(EventTime);
            tracker.OnInventoryItemAdded(EventTime);
            tracker.OnInventoryItemAdded(EventTime);

            src.Progress[LootSource.ChestKey("EltibuleSecretChest")]
                .StartedAt.Should().Be(new DateTimeOffset(EventTime, TimeSpan.Zero));
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// EnableInteractors with non-matching id leaves the bracket open.
    /// </summary>
    [Fact]
    public void EnableInteractors_with_non_matching_id_does_not_close_bracket()
    {
        var (src, tracker, derived) = Build();
        try
        {
            src.OnChestCooldownObserved("TestChest", TimeSpan.FromHours(1));

            tracker.OnInteractionStarted(Start(-200, "TestChest", EventTime));
            tracker.OnEnableInteractors(Enable(-999, EventTime));

            tracker.IsInFlight.Should().BeTrue();

            tracker.OnInventoryItemAdded(EventTime);
            src.Progress.Should().ContainKey(LootSource.ChestKey("TestChest"));
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// EnableInteractors with matching id closes the bracket cleanly.
    /// </summary>
    [Fact]
    public void EnableInteractors_with_matching_id_closes_bracket()
    {
        var (src, tracker, derived) = Build();
        try
        {
            src.OnChestCooldownObserved("TestChest", TimeSpan.FromHours(1));

            tracker.OnInteractionStarted(Start(-200, "TestChest", EventTime));
            tracker.OnInventoryItemAdded(EventTime);
            tracker.OnEnableInteractors(Enable(-200, EventTime));

            tracker.IsInFlight.Should().BeFalse();

            tracker.OnInventoryItemAdded(EventTime);
            src.Progress.Should().HaveCount(1);
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// Portal closes via EndInteraction(id), not EnableInteractors.
    /// </summary>
    [Fact]
    public void EndInteraction_with_matching_id_closes_bracket()
    {
        var (src, tracker, derived) = Build();
        try
        {
            tracker.OnInteractionStarted(Start(-158, "Portal", EventTime));
            tracker.OnInteractionEnded(End(-158, EventTime));

            tracker.IsInFlight.Should().BeFalse();

            tracker.OnInventoryItemAdded(EventTime);
            src.Progress.Should().BeEmpty();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// EndInteraction with non-matching id does not close the bracket.
    /// </summary>
    [Fact]
    public void EndInteraction_with_non_matching_id_does_not_close_bracket()
    {
        var (src, tracker, derived) = Build();
        try
        {
            src.OnChestCooldownObserved("TestChest", TimeSpan.FromHours(1));

            tracker.OnInteractionStarted(Start(-200, "TestChest", EventTime));
            tracker.OnInteractionEnded(End(-999, EventTime));

            tracker.IsInFlight.Should().BeTrue();
            tracker.OnInventoryItemAdded(EventTime);
            src.Progress.Should().ContainKey(LootSource.ChestKey("TestChest"));
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// SummonedFlower entities never emit a close signal. The next
    /// StartInteraction must replace the stale bracket.
    /// </summary>
    [Fact]
    public void Stale_summoned_flower_bracket_is_replaced_by_next_start()
    {
        var (src, tracker, derived) = Build();
        try
        {
            tracker.OnInteractionStarted(Start(10148077, "SummonedFlower4", EventTime));

            src.OnChestCooldownObserved("EltibuleSecretChest", TimeSpan.FromHours(3));
            tracker.OnInteractionStarted(Start(-147, "EltibuleSecretChest", EventTime));
            tracker.OnInventoryItemAdded(EventTime);

            src.Progress.Should().ContainKey(LootSource.ChestKey("EltibuleSecretChest"));
            src.Progress.Should().NotContainKey(LootSource.ChestKey("SummonedFlower4"));
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// LemonTree harvest sequence: ProcessDoDelayLoop with IsInteractorDelayLoop
    /// suppresses the chest commit on the subsequent AddItem.
    /// </summary>
    [Fact]
    public void Harvest_delay_loop_suppresses_chest_commit()
    {
        var (src, tracker, derived) = Build();
        try
        {
            tracker.OnInteractionStarted(Start(9902924, "LemonTree", EventTime));
            tracker.OnDelayLoopStarted(Delay(3, "Gather", "Collecting Fruit...", true, EventTime));
            tracker.OnInventoryItemAdded(EventTime);
            tracker.OnInteractionEnded(End(9902924, EventTime));

            tracker.IsInFlight.Should().BeFalse();
            src.Progress.Should().BeEmpty();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// Self-targeted delay loops (Eat/Drink/UseItem) don't carry the
    /// IsInteractorDelayLoop flag and must not poison a chest bracket.
    /// </summary>
    [Fact]
    public void Self_targeted_delay_loop_does_not_suppress_chest_commit()
    {
        var (src, tracker, derived) = Build();
        try
        {
            src.OnChestCooldownObserved("EltibuleSecretChest", TimeSpan.FromHours(3));

            tracker.OnInteractionStarted(Start(-147, "EltibuleSecretChest", EventTime));
            tracker.OnDelayLoopStarted(Delay(1.5, "Eat", "Using Ranalon Salad", false, EventTime));
            tracker.OnInventoryItemAdded(EventTime);

            src.Progress.Should().ContainKey(LootSource.ChestKey("EltibuleSecretChest"));
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTime(DateTime utcStart) => _now = new DateTimeOffset(utcStart, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
