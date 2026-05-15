using System.Diagnostics;

namespace Mithril.Shared.Reference;

/// <summary>
/// Identifies a kind of game entity that can be navigated to via
/// <see cref="IReferenceNavigator"/>.
/// </summary>
public enum EntityKind
{
    Item,
    Recipe,
    Ability,
    Effect,
    Npc,
    Quest,
    Lorebook,
    Area,
    PlayerTitle,
    StorageVault,
    /// <summary>
    /// A player skill (e.g. <c>"NonfictionWriting"</c>, <c>"Carpentry"</c>). InternalName carries
    /// the skill key from <c>skills.json</c>; the resolver maps it to <see cref="SkillEntry.DisplayName"/>
    /// (e.g. <c>"Non-Fiction Writing"</c>). No browsable Silmarillion skill tab today, so the
    /// resolver covers the rendering case while leaving navigation as a follow-up.
    /// </summary>
    Skill,
    // EntityKind.RecipeIngredientKeyword retired in #318 slice 4 (surface 2) — the
    // item-detail "Used as" 1:N surface is now a provenance popup fed
    // RecipesByIngredientKeywordWithReason directly (no synthetic-kind deep link /
    // query re-derivation). Its mithril://silmarillion/RecipeIngredientKeyword/… route
    // is retired automatically: SilmarillionDeepLinkHandler's generic
    // Enum.TryParse<EntityKind> now rejects the deleted name.

    // EntityKind.ItemKeyword retired in #318 slice 4 (surface 3) — the recipe-detail
    // keyword surface is now a provenance popup fed
    // IReferenceDataService.ItemsByRecipeKeywordSlotWithReason directly (membership +
    // provenance, no query re-derivation). SilmarillionDeepLinkHandler's generic
    // Enum.TryParse<EntityKind> now rejects the "ItemKeyword" route name automatically.
    // NOTE: ItemKeyword was *double-duty* — besides that fan-out, it also backed two
    // legitimate single-keyword 1:1 Items-filter pivots (Ability-detail ItemKeywordReqs /
    // ammo, NPCs-tab Store-cap / Consignment). Those pivots are NOT fan-out and were
    // restored in #327 via the distinct ItemByKeyword kind below — a deliberately
    // different name so the retired ItemKeyword route stays dead.

    /// <summary>
    /// Not an entity per se — a deep-link target for "open the Items tab filtered to items
    /// whose <c>Keywords</c> list contains this tag." InternalName carries a single keyword
    /// tag (e.g. <c>"Sword"</c>, <c>"DenseArrow1"</c>, <c>"Armor"</c>). Dispatched by
    /// ItemByKeywordKindTarget. This is a single-keyword *filter pivot* (one tag → one
    /// filtered view) — a legitimate 1:1 per the #318 chip-vs-popup rule, the symmetric
    /// Items-side twin of <see cref="EffectKeyword"/>. It is **not** the retired #270
    /// <c>ItemKeyword</c> fan-out kind (recipe-slot keyword sets — that is a provenance
    /// popup fed <c>ItemsByRecipeKeywordSlotWithReason</c>; do not reintroduce it). Use
    /// this — not <see cref="Item"/> — when a chip's natural target is a keyword filter
    /// rather than a specific item row. Singleton-payload only (no '+'-joined composite):
    /// the recipe-slot composite form belonged to the retired fan-out kind. Restored in
    /// #327 (the pivot uses degraded to non-navigable plain text in #326 when the shared
    /// ItemKeyword kind was retired for its fan-out use).
    /// </summary>
    ItemByKeyword,

    /// <summary>
    /// Not an entity per se — a deep-link target for "open the Effects tab filtered to effects
    /// whose <c>Keywords</c> list contains this tag." InternalName carries the keyword
    /// (e.g. <c>"FrostShard"</c>, <c>"Buff"</c>). Dispatched by EffectKeywordKindTarget.
    /// Use this — not <see cref="Effect"/> — when a chip's natural target is a keyword
    /// filter rather than a specific effect envelope row.
    /// </summary>
    EffectKeyword,

    /// <summary>
    /// Not an entity per se — a deep-link target for "open the Effects tab filtered to
    /// effects sharing this <see cref="Mithril.Reference.Models.Effects.Effect.StackingType"/>."
    /// InternalName carries the StackingType value (e.g. <c>"Food"</c>, <c>"Snack"</c>).
    /// Dispatched by EffectByStackingTypeKindTarget; powers the Effects-tab "Stacks with"
    /// section's overflow pill — stacking groups like <c>"Food"</c> contain ~326 entries
    /// and would render unscannably as a flat chip cluster.
    /// </summary>
    EffectByStackingType,

    // NpcByArea synthetic kind retired in #318 slice 4, surface 4 — the Areas "NPCs in
    // this area" 1:N surface is now a provenance popup fed
    // IReferenceDataService.NpcsByAreaWithReason directly (no synthetic-kind deep link /
    // query re-derivation). This was the last synthetic kind in the FAN-OUT / 1:N
    // reverse-lookup family #318 targeted (the dual-derivation bug class) — that family
    // is now empty. RecipeIngredientKeyword (#259) and ItemKeyword (#270) above are
    // single-keyword *filter-pivot* kinds (1:1 "open the tab filtered to this concept"),
    // not fan-out sets; the cookbook keeps those as legitimate pending their own slices.
}

/// <summary>
/// A lightweight, serialization-friendly pointer to a specific game entity.
/// Consumers pass this to <see cref="IReferenceNavigator.Open"/> to trigger
/// navigation without coupling to any particular detail-view implementation.
/// </summary>
/// <param name="Kind">The category of entity.</param>
/// <param name="InternalName">The entity's unique internal name (e.g. <c>"CraftedLeatherBoots5"</c>).</param>
public sealed record EntityRef(EntityKind Kind, string InternalName)
{
    public static EntityRef Item(string internalName) => new(EntityKind.Item, internalName);
    public static EntityRef Recipe(string internalName) => new(EntityKind.Recipe, internalName);
    public static EntityRef Ability(string internalName) => new(EntityKind.Ability, internalName);

    /// <summary>
    /// Effect InternalName — equal to the envelope key (e.g. <c>"effect_10003"</c>), lifted
    /// from effects.json by the deserializer. Not a keyword tag and not the human-form
    /// <c>Effect.Name</c> (which collides across many entries). For keyword-based filtering
    /// of the Effects tab use <see cref="EffectKeyword"/> instead.
    /// </summary>
    public static EntityRef Effect(string internalName)
    {
        Debug.Assert(
            internalName.StartsWith("effect_", StringComparison.Ordinal),
            $"EntityRef.Effect expects an envelope-key InternalName like 'effect_10003', got '{internalName}'. "
            + "If you have a keyword tag, use EntityRef.EffectKeyword(...) instead.");
        return new(EntityKind.Effect, internalName);
    }
    /// <summary>
    /// Construct a Npc reference, normalising any area-prefixed slug form (used by quest
    /// fields like <c>Quest.QuestNpc</c> = <c>"AreaSerbule2/NPC_DurstinTallow"</c>) to the
    /// bare envelope-key form used by <c>npcs.json</c> and every downstream consumer
    /// (resolver, kind target, navigator history). No npcs.json envelope key carries a
    /// slash, so the strip is unambiguous.
    /// </summary>
    public static EntityRef Npc(string internalName)
    {
        var slashIdx = internalName.LastIndexOf('/');
        var canonical = slashIdx >= 0 ? internalName.Substring(slashIdx + 1) : internalName;
        return new(EntityKind.Npc, canonical);
    }
    public static EntityRef Quest(string internalName) => new(EntityKind.Quest, internalName);
    public static EntityRef Lorebook(string internalName) => new(EntityKind.Lorebook, internalName);
    public static EntityRef Area(string internalName) => new(EntityKind.Area, internalName);
    public static EntityRef PlayerTitle(string internalName) => new(EntityKind.PlayerTitle, internalName);
    public static EntityRef StorageVault(string internalName) => new(EntityKind.StorageVault, internalName);
    public static EntityRef Skill(string skillKey) => new(EntityKind.Skill, skillKey);
    // EntityRef.RecipeIngredientKeyword retired in #318 slice 4 (surface 2) — the
    // item-detail "Used as" 1:N surface is now a provenance popup fed
    // RecipesByIngredientKeywordWithReason directly.
    // EntityRef.ItemKeyword(string) / ItemKeyword(IReadOnlyList<string>) retired in #318
    // slice 4 (surface 3) — the recipe-detail keyword surface is now a provenance popup
    // fed ItemsByRecipeKeywordSlotWithReason directly.
    // EntityRef.RecipeIngredientItem retired in #318 slice 4 — the Items "Used in" 1:N
    // surface is now a provenance popup fed RecipesByIngredientItemWithReason directly.
    public static EntityRef EffectKeyword(string keyword) => new(EntityKind.EffectKeyword, keyword);
    /// <summary>
    /// Single-keyword Items filter pivot — see <see cref="EntityKind.ItemByKeyword"/>.
    /// Symmetric twin of <see cref="EffectKeyword"/>; restored in #327. NOT the retired
    /// #270 <c>EntityRef.ItemKeyword</c> fan-out factory (recipe-slot keyword sets are a
    /// provenance popup).
    /// </summary>
    public static EntityRef ItemByKeyword(string keyword) => new(EntityKind.ItemByKeyword, keyword);
    public static EntityRef EffectByStackingType(string stackingType) => new(EntityKind.EffectByStackingType, stackingType);
    // EntityRef.NpcByArea retired in #318 slice 4, surface 4 — the Areas "NPCs in this
    // area" 1:N surface is now a provenance popup fed NpcsByAreaWithReason directly.
}

/// <summary>
/// Describes what kind of navigation action produced a <see cref="NavigatedEventArgs"/>.
/// </summary>
public enum NavigationKind
{
    /// <summary>A new entity was opened, pushing onto the back stack and clearing the forward stack.</summary>
    Open,

    /// <summary>The user navigated back through history.</summary>
    Back,

    /// <summary>The user navigated forward through history.</summary>
    Forward,
}

/// <summary>
/// Event data fired by <see cref="IReferenceNavigator.Navigated"/> on every state change.
/// </summary>
/// <param name="Previous">The entity that was current before this navigation, or <see langword="null"/> if there was none.</param>
/// <param name="Current">The entity that is current after this navigation, or <see langword="null"/> if the history is now empty.</param>
/// <param name="Kind">What kind of navigation produced this event.</param>
public sealed record NavigatedEventArgs(
    EntityRef? Previous,
    EntityRef? Current,
    NavigationKind Kind);
