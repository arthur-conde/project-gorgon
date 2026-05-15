using FluentAssertions;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Xunit;

namespace Silmarillion.Tests.Navigation;

public sealed class SilmarillionDeepLinkHandlerTests
{
    [Fact]
    public void TryHandle_ItemKind_OpensItem()
    {
        var nav = new RecordingNavigator();
        var handler = new SilmarillionDeepLinkHandler(nav);

        var handled = handler.TryHandle("item/CraftedLeatherBoots5", diag: null);

        handled.Should().BeTrue();
        nav.LastOpened.Should().Be(EntityRef.Item("CraftedLeatherBoots5"));
    }

    [Fact]
    public void TryHandle_RecipeKind_OpensRecipe()
    {
        var nav = new RecordingNavigator();
        var handler = new SilmarillionDeepLinkHandler(nav);

        var handled = handler.TryHandle("recipe/MakeTomatoSauce", diag: null);

        handled.Should().BeTrue();
        nav.LastOpened.Should().Be(EntityRef.Recipe("MakeTomatoSauce"));
    }

    [Theory]
    [InlineData("npc/Marna")]            // unknown kind segment
    [InlineData("ability/Hatchet")]
    [InlineData("")]                     // empty subPath
    [InlineData("item")]                 // no second segment
    [InlineData("item/")]                // empty payload after slash
    [InlineData("item/has space")]       // illegal payload char
    [InlineData("item/has-hyphen")]      // hyphen rejected by EntityPayloadPattern
    [InlineData("item/extra/segment")]   // extra path segments forbidden
    // #318 slice 4 (surface 2): the RecipeIngredientKeyword synthetic kind was deleted, so
    // its mithril://silmarillion/RecipeIngredientKeyword/… route is retired automatically —
    // Enum.TryParse<EntityKind> no longer recognises the name (no handler edit needed).
    [InlineData("recipeingredientkeyword/Crystal")]
    public void TryHandle_Malformed_ReturnsFalse_AndDoesNotDispatch(string subPath)
    {
        var nav = new RecordingNavigator();
        var handler = new SilmarillionDeepLinkHandler(nav);

        handler.TryHandle(subPath, diag: null).Should().BeFalse();
        nav.LastOpened.Should().BeNull();
    }

    [Theory]
    [InlineData("itemkeyword/Crystal")]
    [InlineData("ItemKeyword/Crystal")]
    [InlineData("itemkeyword/EquipmentSlot:MainHand")]
    public void TryHandle_RetiredItemKeywordRoute_IsRejected(string subPath)
    {
        // #318 slice 4 (surface 3): EntityKind.ItemKeyword was deleted (the recipe-detail
        // keyword surface is now a provenance popup). The generic
        // Enum.TryParse<EntityKind> in SilmarillionDeepLinkHandler now rejects the
        // "ItemKeyword" route name automatically — no handler change was needed, and this
        // locks the retirement so a future re-add of the enum value can't silently
        // resurrect a dead-end deep link.
        var nav = new RecordingNavigator();
        var handler = new SilmarillionDeepLinkHandler(nav);

        handler.TryHandle(subPath, diag: null).Should().BeFalse();
        nav.LastOpened.Should().BeNull();
    }

    [Theory]
    [InlineData("storagevault/NPC_CharlesThompson", "NPC_CharlesThompson")]
    [InlineData("storagevault/*AccountStorage_Serbule", "*AccountStorage_Serbule")]
    [InlineData("StorageVault/*AdminStorage", "*AdminStorage")]
    public void TryHandle_StorageVault_AcceptsStarPrefixedEnvelopeKey(string subPath, string expectedName)
    {
        // #249: StorageVault account-wide / transfer-chest keys carry a meaningful leading
        // '*'. The handler validates with IsValidEnvelopeKey (IsValidInternalName + an
        // optional single leading '*'), so the literal '*' round-trips. Bare internal-name
        // routes (every other kind) are unaffected — they're a strict subset of the grammar.
        var nav = new PermissiveNavigator();
        var handler = new SilmarillionDeepLinkHandler(nav);

        handler.TryHandle(subPath, diag: null).Should().BeTrue();
        nav.LastOpened.Should().Be(EntityRef.StorageVault(expectedName));
    }

    [Fact]
    public void DeepLinkRouter_UrlEncodedStar_DecodesAndDispatchesToStorageVault()
    {
        // End-to-end: mithril://silmarillion/storagevault/%2AAccountStorage_Serbule —
        // Uri.AbsolutePath decodes %2A → '*', the Silmarillion handler accepts it.
        var nav = new PermissiveNavigator();
        var router = new Mithril.Shared.Modules.DeepLinkRouter(
            new[] { new SilmarillionDeepLinkHandler(nav) });

        router.Handle("mithril://silmarillion/storagevault/%2AAccountStorage_Serbule")
            .Should().BeTrue();
        nav.LastOpened.Should().Be(EntityRef.StorageVault("*AccountStorage_Serbule"));
    }

    [Fact]
    public void DeepLinkPayload_StillRejectsSeparatorsAndWhitespace_EvenWithStarAllowed()
    {
        // The '*'-permissive envelope-key grammar must NOT regress the URI-safety intent:
        // separators / whitespace / a non-leading or doubled '*' stay rejected. The
        // original strict internal-name grammar (every other handler) is unchanged.
        Mithril.Shared.Modules.DeepLinkPayload.IsValidEnvelopeKey("*AccountStorage_Serbule").Should().BeTrue();
        Mithril.Shared.Modules.DeepLinkPayload.IsValidEnvelopeKey("NPC_Joeh").Should().BeTrue();
        Mithril.Shared.Modules.DeepLinkPayload.IsValidEnvelopeKey("a*b").Should().BeFalse("'*' is only valid as a single leading char");
        Mithril.Shared.Modules.DeepLinkPayload.IsValidEnvelopeKey("**x").Should().BeFalse();
        Mithril.Shared.Modules.DeepLinkPayload.IsValidEnvelopeKey("has space").Should().BeFalse();
        Mithril.Shared.Modules.DeepLinkPayload.IsValidEnvelopeKey("a/b").Should().BeFalse();
        Mithril.Shared.Modules.DeepLinkPayload.IsValidEnvelopeKey("").Should().BeFalse();
        Mithril.Shared.Modules.DeepLinkPayload.IsValidEnvelopeKey("*").Should().BeFalse("a lone '*' has no key body");
        Mithril.Shared.Modules.DeepLinkPayload.IsValidInternalName("*AccountStorage_Serbule").Should().BeFalse();
    }

    [Fact]
    public void TryHandle_PayloadOverLengthCap_IsRejected()
    {
        var nav = new RecordingNavigator();
        var handler = new SilmarillionDeepLinkHandler(nav);

        var tooLong = new string('A', 129);
        handler.TryHandle($"item/{tooLong}", diag: null).Should().BeFalse();
        nav.LastOpened.Should().BeNull();
    }

    [Fact]
    public void Action_IsSilmarillion()
    {
        var nav = new RecordingNavigator();
        new SilmarillionDeepLinkHandler(nav).Action.Should().Be("silmarillion");
    }

    [Theory]
    [InlineData("ITEM/CraftedLeatherBoots5")]
    [InlineData("Item/CraftedLeatherBoots5")]
    [InlineData("iTeM/CraftedLeatherBoots5")]
    public void TryHandle_KindIsCaseInsensitive(string subPath)
    {
        var nav = new RecordingNavigator();
        var handler = new SilmarillionDeepLinkHandler(nav);

        handler.TryHandle(subPath, diag: null).Should().BeTrue();
        nav.LastOpened.Should().Be(EntityRef.Item("CraftedLeatherBoots5"));
    }

    [Theory]
    [InlineData("NpcByArea/AreaSerbule")]   // synthetic kind retired in #318 slice 4, surface 4
    [InlineData("npcbyarea/AreaSerbule")]   // case-insensitive: still unknown
    public void TryHandle_RetiredNpcByAreaRoute_IsRejected(string subPath)
    {
        // #318 slice 4, surface 4 — deleting EntityKind.NpcByArea retires the generic
        // mithril://silmarillion/NpcByArea/... route for free: Enum.TryParse<EntityKind>
        // no longer recognises "NpcByArea", so the handler rejects it before any
        // navigation. (The "NPCs in this area" surface is now a provenance popup, never a
        // navigable route.)
        Enum.TryParse<EntityKind>("NpcByArea", ignoreCase: true, out _).Should().BeFalse(
            "the synthetic kind was deleted — the generic route dispatch must reject it.");

        var nav = new PermissiveNavigator();
        var handler = new SilmarillionDeepLinkHandler(nav);

        handler.TryHandle(subPath, diag: null).Should().BeFalse();
        nav.LastOpened.Should().BeNull();
    }

    [Theory]
    [InlineData("npc/Marna", EntityKind.Npc, "Marna")]
    [InlineData("ability/Hatchet", EntityKind.Ability, "Hatchet")]
    [InlineData("quest/RuminationsOfAYoungMan", EntityKind.Quest, "RuminationsOfAYoungMan")]
    [InlineData("effect/effect_10003", EntityKind.Effect, "effect_10003")]
    [InlineData("area/AreaSerbule", EntityKind.Area, "AreaSerbule")]
    public void TryHandle_DispatchesAnyEntityKindTheNavigatorAccepts(
        string subPath, EntityKind expectedKind, string expectedName)
    {
        var nav = new PermissiveNavigator();
        var handler = new SilmarillionDeepLinkHandler(nav);

        handler.TryHandle(subPath, diag: null).Should().BeTrue();
        nav.LastOpened.Should().Be(new EntityRef(expectedKind, expectedName));
    }

    // Models the production SilmarillionReferenceNavigator: only kinds that have
    // a registered IReferenceKindTarget are openable. Items + Recipes are the
    // currently-shipping Bucket-A tabs.
    private sealed class RecordingNavigator : IReferenceNavigator
    {
        public EntityRef? LastOpened { get; private set; }
        public EntityRef? Current => LastOpened;
        public bool CanGoBack => false;
        public bool CanGoForward => false;
        public bool CanOpen(EntityRef reference) =>
            reference.Kind is EntityKind.Item or EntityKind.Recipe;
        public void Open(EntityRef reference) => LastOpened = reference;
        public void Back() { }
        public void Forward() { }
        public event EventHandler<NavigatedEventArgs>? Navigated { add { } remove { } }
    }

    // Opens any kind — used to prove the handler delegates to the navigator's
    // registry rather than enumerating kinds itself.
    private sealed class PermissiveNavigator : IReferenceNavigator
    {
        public EntityRef? LastOpened { get; private set; }
        public EntityRef? Current => LastOpened;
        public bool CanGoBack => false;
        public bool CanGoForward => false;
        public bool CanOpen(EntityRef reference) => true;
        public void Open(EntityRef reference) => LastOpened = reference;
        public void Back() { }
        public void Forward() { }
        public event EventHandler<NavigatedEventArgs>? Navigated { add { } remove { } }
    }
}
