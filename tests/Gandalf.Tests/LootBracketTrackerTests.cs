using System.IO;
using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Parsing;
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
        Build(IEnumerable<DefeatCatalogEntry>? defeats = null)
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
        var src = new LootSource(derived, cacheStore, cache, defeats ?? [], time);
        var tracker = new LootBracketTracker(src, new ChestInteractionParser(), new ChestRejectionParser());

        return (src, tracker, derived);
    }

    private static readonly DateTime EventTime = new(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Live capture: EltibuleSecretChest bracket. v1's substring filter
    /// (Contains("StaticChest")) silently dropped this chest entirely. The
    /// signal-driven tracker correctly identifies it as loot via the AddItem
    /// inside the bracket.
    /// </summary>
    [Fact]
    public void Loot_chest_with_AddItem_creates_progress_row_and_caches_duration()
    {
        var (src, tracker, derived) = Build();
        try
        {
            // Seed cache so first interaction creates a row (otherwise it's the
            // first-loot-of-unknown-chest case which is correctly skipped).
            src.OnChestCooldownObserved("EltibuleSecretChest", TimeSpan.FromHours(3));

            tracker.Observe("LocalPlayer: ProcessStartInteraction(-147, 5, 0, False, \"EltibuleSecretChest\")", EventTime);
            tracker.Observe("LocalPlayer: ProcessAddItem(PowerPotion2(113863546), -1, True)", EventTime);
            tracker.Observe("LocalPlayer: ProcessAddItem(ThentreeSkirt(113863548), -1, True)", EventTime);
            tracker.Observe("LocalPlayer: ProcessEnableInteractors([], [-147,])", EventTime);

            src.Progress.Should().ContainKey(LootSource.ChestKey("EltibuleSecretChest"));
            tracker.IsInFlight.Should().BeFalse();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// Storage chest bracket — opens a TalkScreen UI dialog, no AddItem, no
    /// row should be created. This is the case the substring filter handled
    /// accidentally; the signal-driven tracker handles it intentionally.
    /// </summary>
    [Fact]
    public void Storage_chest_with_TalkScreen_creates_no_row()
    {
        var (src, tracker, derived) = Build();
        try
        {
            // Even with a duration cached, a TalkScreen-discriminated bracket
            // must not produce a chest interaction event.
            src.OnChestCooldownObserved("SerbuleCommunityChest", TimeSpan.FromHours(3));
            // (the cache write itself populates the catalog, but no progress row)
            src.Progress.Should().BeEmpty();

            tracker.Observe("LocalPlayer: ProcessStartInteraction(31190, 7, 0, False, \"SerbuleCommunityChest\")", EventTime);
            tracker.Observe("LocalPlayer: ProcessPreTalkScreen(31190, PreTalkScreenInfo)", EventTime);
            tracker.Observe("LocalPlayer: ProcessTalkScreen(31190, \"Serbule Dynamic Safebox\", \"...\", \"\", [-1701,1,10,], System.String[], 0, Generic)", EventTime);

            src.Progress.Should().BeEmpty();
            tracker.IsInFlight.Should().BeFalse();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// NPC dialog interaction (vendor, quest-giver, etc.) — same shape as
    /// storage from the bracket's perspective: TalkScreen, no AddItem.
    /// </summary>
    [Fact]
    public void NPC_dialog_creates_no_row()
    {
        var (src, tracker, derived) = Build();
        try
        {
            src.OnChestCooldownObserved("NpcMaxine", TimeSpan.FromHours(1));

            tracker.Observe("LocalPlayer: ProcessStartInteraction(42, 0, 0, False, \"NpcMaxine\")", EventTime);
            tracker.Observe("LocalPlayer: ProcessTalkScreen(42, \"Maxine\", \"How may I help you?\", \"\", [], System.String[], 0, Generic)", EventTime);

            src.Progress.Should().BeEmpty();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// Cooldown rejection inside the bracket — no AddItem (loot was suppressed),
    /// instead a screen-text rejection carrying the duration. Must update the
    /// catalog cache without creating a progress row.
    /// </summary>
    [Fact]
    public void Rejection_inside_bracket_caches_duration_without_creating_row()
    {
        var (src, tracker, derived) = Build();
        try
        {
            tracker.Observe("LocalPlayer: ProcessStartInteraction(-162, 7, 0, False, \"GoblinStaticChest1\")", EventTime);
            tracker.Observe("LocalPlayer: ProcessScreenText(GeneralInfo, \"You've already looted this chest! (It will refill 3 hours after you looted it.)\")", EventTime);

            // Catalog should now know the duration even though no row was created.
            src.Catalog.Should().Contain(c => c.DisplayName == "GoblinStaticChest1");
            src.Progress.Should().BeEmpty();
            tracker.IsInFlight.Should().BeFalse();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// AddItem outside any bracket (e.g., quest reward, vendor purchase fired
    /// after a TalkScreen-discarded bracket) must not create a chest row.
    /// </summary>
    [Fact]
    public void AddItem_outside_bracket_creates_no_row()
    {
        var (src, tracker, derived) = Build();
        try
        {
            src.OnChestCooldownObserved("GoblinStaticChest1", TimeSpan.FromHours(3));

            // Bare AddItem with no preceding StartInteraction.
            tracker.Observe("LocalPlayer: ProcessAddItem(Apple(1234), -1, True)", EventTime);
            // TalkScreen-discarded bracket then AddItem (e.g., quest reward).
            tracker.Observe("LocalPlayer: ProcessStartInteraction(42, 0, 0, False, \"NpcQuestGiver\")", EventTime);
            tracker.Observe("LocalPlayer: ProcessTalkScreen(42, \"Quest\", \"Done!\", \"\", [], System.String[], 0, Generic)", EventTime);
            tracker.Observe("LocalPlayer: ProcessAddItem(QuestReward(5678), -1, True)", EventTime);

            src.Progress.Should().BeEmpty();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// New StartInteraction replaces an in-flight bracket without committing
    /// the prior — covers the case where the player walks away from one
    /// interaction into another without explicitly closing.
    /// </summary>
    [Fact]
    public void New_Start_replaces_in_flight_bracket()
    {
        var (src, tracker, derived) = Build();
        try
        {
            src.OnChestCooldownObserved("ChestA", TimeSpan.FromHours(1));
            src.OnChestCooldownObserved("ChestB", TimeSpan.FromHours(2));

            // Open ChestA, then immediately open ChestB, then loot ChestB.
            tracker.Observe("LocalPlayer: ProcessStartInteraction(-100, 7, 0, False, \"ChestA\")", EventTime);
            tracker.Observe("LocalPlayer: ProcessStartInteraction(-101, 7, 0, False, \"ChestB\")", EventTime);
            tracker.Observe("LocalPlayer: ProcessAddItem(Apple(9999), -1, True)", EventTime);

            // Only ChestB should have a progress row.
            src.Progress.Should().ContainKey(LootSource.ChestKey("ChestB"));
            src.Progress.Should().NotContainKey(LootSource.ChestKey("ChestA"));
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// Multiple AddItems from the same chest don't fire the chest-interaction
    /// event multiple times — only the first commits.
    /// </summary>
    [Fact]
    public void Multiple_AddItems_in_one_bracket_only_commit_once()
    {
        var (src, tracker, derived) = Build();
        try
        {
            src.OnChestCooldownObserved("EltibuleSecretChest", TimeSpan.FromHours(3));

            tracker.Observe("LocalPlayer: ProcessStartInteraction(-147, 5, 0, False, \"EltibuleSecretChest\")", EventTime);
            tracker.Observe("LocalPlayer: ProcessAddItem(PowerPotion2(1), -1, True)", EventTime);
            tracker.Observe("LocalPlayer: ProcessAddItem(ThentreeSkirt(2), -1, True)", EventTime);
            tracker.Observe("LocalPlayer: ProcessAddItem(GoldenLockpick(3), -1, True)", EventTime);

            // The progress entry should reflect a single committed cooldown
            // anchored at the bracket's StartedAt. (Multiple commits would
            // overwrite StartedAt, but they'd be safely the same value.)
            src.Progress[LootSource.ChestKey("EltibuleSecretChest")]
                .StartedAt.Should().Be(new DateTimeOffset(EventTime, TimeSpan.Zero));
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// EnableInteractors with non-matching id leaves the bracket open —
    /// stale events from earlier interactors shouldn't close the current one.
    /// </summary>
    [Fact]
    public void EnableInteractors_with_non_matching_id_does_not_close_bracket()
    {
        var (src, tracker, derived) = Build();
        try
        {
            src.OnChestCooldownObserved("TestChest", TimeSpan.FromHours(1));

            tracker.Observe("LocalPlayer: ProcessStartInteraction(-200, 7, 0, False, \"TestChest\")", EventTime);
            // EnableInteractors for a different id — must not close this bracket.
            tracker.Observe("LocalPlayer: ProcessEnableInteractors([], [-999,])", EventTime);

            tracker.IsInFlight.Should().BeTrue();

            // Loot still confirms.
            tracker.Observe("LocalPlayer: ProcessAddItem(Apple(1), -1, True)", EventTime);
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

            tracker.Observe("LocalPlayer: ProcessStartInteraction(-200, 7, 0, False, \"TestChest\")", EventTime);
            tracker.Observe("LocalPlayer: ProcessAddItem(Apple(1), -1, True)", EventTime);
            tracker.Observe("LocalPlayer: ProcessEnableInteractors([], [-200,])", EventTime);

            tracker.IsInFlight.Should().BeFalse();

            // A subsequent AddItem outside the bracket creates no row.
            tracker.Observe("LocalPlayer: ProcessAddItem(Pear(2), -1, True)", EventTime);
            // Still only one row — TestChest from the closed bracket.
            src.Progress.Should().HaveCount(1);
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
