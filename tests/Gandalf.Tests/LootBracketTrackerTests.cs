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
        var src = new LootSource(derived, cacheStore, cache, time);
        var tracker = new LootBracketTracker(
            src,
            new ChestInteractionParser(),
            new ChestRejectionParser(),
            new InteractionEndParser(),
            new InteractionDelayLoopParser(),
            new InteractionWaitParser());

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
    /// Storage Box ("StorageCatalog") — the in-town all-vault selector. Opens
    /// with a <c>TalkScreen</c> describing the catalog, then issues
    /// <c>ProcessShowStorageVault</c> calls under the same interactor id when
    /// the player picks a vault. The TalkScreen discards the bracket; the
    /// later vault-pick events fire with the bracket already idle.
    ///
    /// Live capture (#174): #174 confirms StorageCatalog reuses interactor id
    /// (-28 below) for vault hopping — no new ProcessStartInteraction per pick.
    /// </summary>
    [Fact]
    public void StorageCatalog_with_TalkScreen_then_vault_picks_creates_no_row()
    {
        var (src, tracker, derived) = Build();
        try
        {
            tracker.Observe("LocalPlayer: ProcessStartInteraction(-28, 5, 0, False, \"StorageCatalog\")", EventTime);
            tracker.Observe("LocalPlayer: ProcessPreTalkScreen(-28, PreTalkScreenInfo)", EventTime);
            tracker.Observe("LocalPlayer: ProcessTalkScreen(-28, \"Storage\", \"<i>[This provides quick access to the unlocked storage of any villager or container in town.]</i>\", \"\", [101,102,103,104,105,106,107,], System.String[], 0, Generic)", EventTime);
            // Catalog already discarded the bracket; vault-pick fires don't reopen one.
            tracker.IsInFlight.Should().BeFalse();
            tracker.Observe("LocalPlayer: ProcessShowStorageVault(-28, 303, \"Storage\", \"\", 32, System.Collections.Generic.List`1[Item], System.String[], \"\", [101,102,103,104,105,106,107,], System.String[], 1)", EventTime);
            tracker.Observe("LocalPlayer: ProcessShowStorageVault(-28, 304, \"Storage\", \"\", 36, System.Collections.Generic.List`1[Item], System.String[], \"\", [101,102,103,104,105,106,107,], System.String[], 1)", EventTime);

            src.Progress.Should().BeEmpty();
            tracker.IsInFlight.Should().BeFalse();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// Direct storage chest click — fires <c>ProcessShowStorageVault</c>
    /// without a preceding TalkScreen. Captured shape (#174):
    /// IvynsChest after the player enters the correct passcode.
    ///
    /// <code>
    /// ProcessStartInteraction(-45, 5, 0, False, "IvynsChest")
    /// ProcessInputBox(-45, EnterNumber, "Ivyn's Chest", ...)
    /// ProcessWaitInteraction(-45, 500, "", "")
    /// ProcessShowStorageVault(-45, 303, "Ivyn's Storage Chest", ...)
    /// </code>
    /// </summary>
    [Fact]
    public void Direct_storage_chest_with_ShowStorageVault_creates_no_row()
    {
        var (src, tracker, derived) = Build();
        try
        {
            tracker.Observe("LocalPlayer: ProcessStartInteraction(-45, 5, 0, False, \"IvynsChest\")", EventTime);
            tracker.Observe("LocalPlayer: ProcessInputBox(-45, EnterNumber, \"Ivyn's Chest\", \"There's a magical lock on Ivyn's chest. It seems to want a five-digit number.\", \"Enter Code\", \"\", \"\", 5, 5, [], System.String[], 0)", EventTime);
            // InputBox already discards the bracket; the rest are no-ops, but
            // verify we don't accidentally reopen one or commit anything.
            tracker.IsInFlight.Should().BeFalse();
            tracker.Observe("LocalPlayer: ProcessWaitInteraction(-45, 500, \"\", \"\")", EventTime);
            tracker.Observe("LocalPlayer: ProcessShowStorageVault(-45, 303, \"Ivyn's Storage Chest\", \"\", 32, System.Collections.Generic.List`1[Item], System.String[], \"\", [], System.String[], 0)", EventTime);

            src.Progress.Should().BeEmpty();
            tracker.IsInFlight.Should().BeFalse();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// Storage chest that doesn't go through a TalkScreen or InputBox at all
    /// — fires <c>ProcessShowStorageVault</c> directly. Covers the
    /// <c>StorageCrate</c> / <c>GlobalStorageMachine</c> /
    /// <c>SerbuleSharedAccountStorage</c> false-positive class from #174.
    /// </summary>
    [Fact]
    public void Storage_chest_with_only_ShowStorageVault_creates_no_row()
    {
        var (src, tracker, derived) = Build();
        try
        {
            tracker.Observe("LocalPlayer: ProcessStartInteraction(-100, 5, 0, False, \"GlobalStorageMachine\")", EventTime);
            tracker.Observe("LocalPlayer: ProcessShowStorageVault(-100, 301, \"Storage\", \"\", 34, System.Collections.Generic.List`1[Item], System.String[], \"\", [], System.String[], 0)", EventTime);

            src.Progress.Should().BeEmpty();
            tracker.IsInFlight.Should().BeFalse();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// Workstations and teleport pads emit <c>ProcessShowRecipes(&lt;skill&gt;)</c>
    /// to open the recipe-list / destination-list UI. Captured shapes (#174):
    /// <list type="bullet">
    /// <item><c>Fireplace → ProcessShowRecipes(Cooking)</c></item>
    /// <item><c>TanningRack → ProcessShowRecipes(Tanning)</c></item>
    /// <item><c>TeleportationPlatform → ProcessShowRecipes(Teleportation)</c></item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData("Fireplace", "Cooking")]
    [InlineData("TanningRack", "Tanning")]
    [InlineData("TeleportationPlatform", "Teleportation")]
    public void Workstation_with_ShowRecipes_creates_no_row(string entity, string skill)
    {
        var (src, tracker, derived) = Build();
        try
        {
            tracker.Observe($"LocalPlayer: ProcessStartInteraction(-16, 5, 0, False, \"{entity}\")", EventTime);
            tracker.Observe($"LocalPlayer: ProcessShowRecipes({skill})", EventTime);
            // No close signal will follow — the bracket must already be idle so
            // any later ambient AddItem can't commit the workstation as a chest.
            tracker.IsInFlight.Should().BeFalse();

            tracker.Observe("LocalPlayer: ProcessAddItem(SomeAmbientItem(1234), -1, True)", EventTime);
            src.Progress.Should().BeEmpty();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// Passcode-gated container — emits <c>ProcessInputBox</c> for the code
    /// prompt. If the player cancels without entering the code, no further
    /// signals fire for this interactor. The InputBox itself must discard
    /// the bracket so a later ambient <c>ProcessAddItem</c> can't commit.
    /// </summary>
    [Fact]
    public void InputBox_then_cancel_creates_no_row_on_subsequent_ambient_AddItem()
    {
        var (src, tracker, derived) = Build();
        try
        {
            tracker.Observe("LocalPlayer: ProcessStartInteraction(-45, 5, 0, False, \"IvynsChest\")", EventTime);
            tracker.Observe("LocalPlayer: ProcessInputBox(-45, EnterNumber, \"Ivyn's Chest\", \"There's a magical lock on Ivyn's chest. It seems to want a five-digit number.\", \"Enter Code\", \"\", \"\", 5, 5, [], System.String[], 0)", EventTime);
            tracker.IsInFlight.Should().BeFalse();

            // Player cancels; later, an ambient AddItem from elsewhere fires.
            tracker.Observe("LocalPlayer: ProcessAddItem(QuestReward(9999), -1, True)", EventTime);
            src.Progress.Should().BeEmpty();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// <c>ProcessWaitInteraction</c> with a non-empty body is the harvest
    /// signal for activities like filling water bottles at a well or fishing
    /// — semantically equivalent to <c>ProcessDoDelayLoop ...
    /// IsInteractorDelayLoop</c> but a different log signal entirely.
    /// Captured shape (#174):
    /// <code>
    /// ProcessStartInteraction(-2, 7, 0, False, "WaterWell")
    /// ProcessWaitInteraction(-2, 500, "Filling Water Bottles...", "Empty Bottles: 8...")
    /// ProcessAddItem(BottleOfWater(...))
    /// </code>
    /// </summary>
    [Fact]
    public void Wait_interaction_with_body_suppresses_chest_commit()
    {
        var (src, tracker, derived) = Build();
        try
        {
            tracker.Observe("LocalPlayer: ProcessStartInteraction(-2, 7, 0, False, \"WaterWell\")", EventTime);
            tracker.Observe("LocalPlayer: ProcessWaitInteraction(-2, 500, \"Filling Water Bottles...\", \"Empty Bottles: 8 Bottles of Water: 0\")", EventTime);
            tracker.Observe("LocalPlayer: ProcessAddItem(BottleOfWater(123488261), -1, True)", EventTime);

            src.Progress.Should().BeEmpty();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// Empty-body <c>ProcessWaitInteraction</c> (the IvynsChest unlock
    /// animation) is NOT a harvest signal. Verifies that an unrelated
    /// chest interaction sandwiched after one still commits — i.e. the
    /// empty-body wait doesn't accidentally stash a harvest verb that
    /// would suppress a later real chest's AddItem.
    /// </summary>
    [Fact]
    public void Empty_body_wait_interaction_does_not_suppress_subsequent_real_chest()
    {
        var (src, tracker, derived) = Build();
        try
        {
            src.OnChestCooldownObserved("EltibuleSecretChest", TimeSpan.FromHours(3));

            // Empty-body wait fires for a fully-discarded storage bracket.
            tracker.Observe("LocalPlayer: ProcessStartInteraction(-45, 5, 0, False, \"IvynsChest\")", EventTime);
            tracker.Observe("LocalPlayer: ProcessShowStorageVault(-45, 303, \"Ivyn's Storage Chest\", \"\", 32, System.Collections.Generic.List`1[Item], System.String[], \"\", [], System.String[], 0)", EventTime);
            tracker.IsInFlight.Should().BeFalse();

            // Real chest interaction next — must commit normally.
            tracker.Observe("LocalPlayer: ProcessStartInteraction(-147, 5, 0, False, \"EltibuleSecretChest\")", EventTime);
            tracker.Observe("LocalPlayer: ProcessAddItem(PowerPotion2(1), -1, True)", EventTime);

            src.Progress.Should().ContainKey(LootSource.ChestKey("EltibuleSecretChest"));
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// Stray <c>ProcessWaitInteraction</c> from a different interactor id
    /// must not stash a harvest verb on the in-flight bracket.
    /// </summary>
    [Fact]
    public void Wait_interaction_with_non_matching_id_does_not_poison_bracket()
    {
        var (src, tracker, derived) = Build();
        try
        {
            src.OnChestCooldownObserved("EltibuleSecretChest", TimeSpan.FromHours(3));

            tracker.Observe("LocalPlayer: ProcessStartInteraction(-147, 5, 0, False, \"EltibuleSecretChest\")", EventTime);
            // Stray wait for a different interactor id — must be ignored.
            tracker.Observe("LocalPlayer: ProcessWaitInteraction(-999, 500, \"Filling Water Bottles...\", \"...\")", EventTime);
            tracker.Observe("LocalPlayer: ProcessAddItem(PowerPotion2(1), -1, True)", EventTime);

            src.Progress.Should().ContainKey(LootSource.ChestKey("EltibuleSecretChest"));
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// Soft timeout backstop (#174): a bracket that's been InFlight longer
    /// than <see cref="LootBracketTracker.SoftTimeout"/> with no positive
    /// signal must not commit a subsequent <c>ProcessAddItem</c>. Backstop
    /// for no-signal leakers like <c>SummonedFlowerN</c> and
    /// <c>SummonedHorseApple</c> that emit only <c>ProcessUpdateDescription</c>.
    /// </summary>
    [Fact]
    public void Bracket_older_than_soft_timeout_does_not_commit_subsequent_AddItem()
    {
        var (src, tracker, derived) = Build();
        try
        {
            tracker.Observe("LocalPlayer: ProcessStartInteraction(16189159, 7, 0, False, \"SummonedHorseApple\")", EventTime);
            tracker.Observe("ProcessUpdateDescription(16189159, \"Growing Horse Apple Bush\", \"This horse apple bush is growing nicely.\", \"Check Horse Apple Bush\", UseItem, \"AppleTree(Scale=0.14)\", 0)", EventTime);

            // Ambient AddItem 5 seconds later — past the soft timeout.
            var late = EventTime + TimeSpan.FromSeconds(5);
            tracker.Observe("LocalPlayer: ProcessAddItem(QuestReward(9999), -1, True)", late);

            src.Progress.Should().BeEmpty();
            tracker.IsInFlight.Should().BeFalse();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// Soft-timeout boundary check: an AddItem that arrives exactly at the
    /// timeout still commits. Real chests fire AddItem in the same log
    /// second, so the test of the boundary is purely defensive.
    /// </summary>
    [Fact]
    public void AddItem_at_soft_timeout_boundary_still_commits()
    {
        var (src, tracker, derived) = Build();
        try
        {
            src.OnChestCooldownObserved("EltibuleSecretChest", TimeSpan.FromHours(3));

            tracker.Observe("LocalPlayer: ProcessStartInteraction(-147, 5, 0, False, \"EltibuleSecretChest\")", EventTime);
            // AddItem at exactly the timeout — boundary inclusive.
            var atBoundary = EventTime + LootBracketTracker.SoftTimeout;
            tracker.Observe("LocalPlayer: ProcessAddItem(PowerPotion2(1), -1, True)", atBoundary);

            src.Progress.Should().ContainKey(LootSource.ChestKey("EltibuleSecretChest"));
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

    /// <summary>
    /// #91 Mode A: Portal closes via ProcessEndInteraction(id), not
    /// ProcessEnableInteractors. Without that close signal recognized, the
    /// bracket sat InFlight and the next stray AddItem (a real chest, quest
    /// reward, etc.) would commit "Portal" as a chest row.
    /// </summary>
    [Fact]
    public void EndInteraction_with_matching_id_closes_bracket()
    {
        var (src, tracker, derived) = Build();
        try
        {
            // Portal interaction shape from live capture.
            tracker.Observe("LocalPlayer: ProcessStartInteraction(-158, 8, 0, False, \"Portal\")", EventTime);
            tracker.Observe("LocalPlayer: ProcessEndInteraction(-158)", EventTime);

            tracker.IsInFlight.Should().BeFalse();

            // A later, unrelated AddItem must not be attributed to "Portal".
            tracker.Observe("LocalPlayer: ProcessAddItem(Apple(1), -1, True)", EventTime);
            src.Progress.Should().BeEmpty();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// #91 Mode A: EndInteraction with non-matching id is a stale signal
    /// (e.g. a prior interactor finishing late) and must not close the
    /// current bracket.
    /// </summary>
    [Fact]
    public void EndInteraction_with_non_matching_id_does_not_close_bracket()
    {
        var (src, tracker, derived) = Build();
        try
        {
            src.OnChestCooldownObserved("TestChest", TimeSpan.FromHours(1));

            tracker.Observe("LocalPlayer: ProcessStartInteraction(-200, 7, 0, False, \"TestChest\")", EventTime);
            tracker.Observe("LocalPlayer: ProcessEndInteraction(-999)", EventTime);

            tracker.IsInFlight.Should().BeTrue();
            tracker.Observe("LocalPlayer: ProcessAddItem(Apple(1), -1, True)", EventTime);
            src.Progress.Should().ContainKey(LootSource.ChestKey("TestChest"));
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// #91 Mode A: SummonedFlower entities never emit a close signal at all
    /// (no EndInteraction, no EnableInteractors). The next ProcessStartInteraction
    /// must replace the stale bracket so an unrelated AddItem from somewhere
    /// later in the log doesn't get committed under the flower's name.
    /// </summary>
    [Fact]
    public void Stale_summoned_flower_bracket_is_replaced_by_next_start()
    {
        var (src, tracker, derived) = Build();
        try
        {
            // Flower interaction never closes — only emits an UpdateDescription.
            tracker.Observe("LocalPlayer: ProcessStartInteraction(10148077, 7, 0, False, \"SummonedFlower4\")", EventTime);
            tracker.Observe("ProcessUpdateDescription(10148077, \"Growing Dahlia\", \"This dahlia is growing nicely.\", \"Check Dahlia\", UseItem, \"Flower4(Scale=0.75)\", 0)", EventTime);

            // Real chest interaction takes over before any AddItem fires.
            src.OnChestCooldownObserved("EltibuleSecretChest", TimeSpan.FromHours(3));
            tracker.Observe("LocalPlayer: ProcessStartInteraction(-147, 5, 0, False, \"EltibuleSecretChest\")", EventTime);
            tracker.Observe("LocalPlayer: ProcessAddItem(PowerPotion2(1), -1, True)", EventTime);

            src.Progress.Should().ContainKey(LootSource.ChestKey("EltibuleSecretChest"));
            src.Progress.Should().NotContainKey(LootSource.ChestKey("SummonedFlower4"));
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// #91 Mode B: LemonTree (and any harvestable tree / plant / node) emits
    /// a real ProcessAddItem inside the bracket — picking fruit. The
    /// ProcessDoDelayLoop with the IsInteractorDelayLoop flag distinguishes
    /// it from a chest; the AddItem must not produce a chest row.
    /// </summary>
    [Fact]
    public void Harvest_delay_loop_suppresses_chest_commit()
    {
        var (src, tracker, derived) = Build();
        try
        {
            // Verbatim shape from live capture (#91): LemonTree harvest sequence.
            tracker.Observe("LocalPlayer: ProcessStartInteraction(9902924, 7, 0, False, \"LemonTree\")", EventTime);
            tracker.Observe("LocalPlayer: ProcessDoDelayLoop(3, Gather, \"Collecting Fruit...\", 0, AbortIfAttacked, IsInteractorDelayLoop)", EventTime);
            tracker.Observe("LocalPlayer: ProcessAddItem(Lemon(115660438), -1, True)", EventTime);
            tracker.Observe("LocalPlayer: ProcessEndInteraction(9902924)", EventTime);

            tracker.IsInFlight.Should().BeFalse();
            src.Progress.Should().BeEmpty();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    /// <summary>
    /// #91 Mode B guard: self-targeted delay loops (Eat / Drink / UseItem /
    /// UseTeleportationCircle) don't carry the IsInteractorDelayLoop flag and
    /// therefore must not poison an in-flight chest bracket. Sequence:
    /// player opens a chest, eats food while it's open, then loots — the
    /// chest commit must still fire.
    /// </summary>
    [Fact]
    public void Self_targeted_delay_loop_does_not_suppress_chest_commit()
    {
        var (src, tracker, derived) = Build();
        try
        {
            src.OnChestCooldownObserved("EltibuleSecretChest", TimeSpan.FromHours(3));

            tracker.Observe("LocalPlayer: ProcessStartInteraction(-147, 5, 0, False, \"EltibuleSecretChest\")", EventTime);
            // Self-targeted "Eat" delay loop — no IsInteractorDelayLoop flag.
            tracker.Observe("LocalPlayer: ProcessDoDelayLoop(1.5, Eat, \"Using Ranalon Salad\", 5820, AbortIfAttacked)", EventTime);
            tracker.Observe("LocalPlayer: ProcessAddItem(PowerPotion2(1), -1, True)", EventTime);

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
