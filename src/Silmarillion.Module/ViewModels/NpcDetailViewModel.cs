using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Mithril.Reference.Models.Npcs;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;

namespace Silmarillion.ViewModels;

/// <summary>
/// Read-only projection of an <see cref="Npc"/> for the Silmarillion NPCs tab detail pane.
/// Hostable in both the master-detail right pane and the popup <see cref="Silmarillion.Views.NpcDetailWindow"/>.
/// Cross-link projections (taught recipes, sold items, given quests, gift preferences) are
/// supplied by the page-level view-model, which has access to the reverse-lookup indices on
/// <see cref="Mithril.Shared.Reference.IReferenceDataService"/> and the navigator's
/// <c>CanOpen</c> for the <see cref="EntityChipVm.IsNavigable"/> flag.
/// </summary>
public sealed class NpcDetailViewModel
{
    private readonly IEntityNameResolver _nameResolver;

    public NpcDetailViewModel(
        Npc npc,
        string internalName,
        IEntityNameResolver nameResolver,
        IReadOnlyList<NpcServiceRow> services,
        IReadOnlyList<EntityChipVm> taughtRecipes,
        IReadOnlyList<EntityChipVm> taughtAbilities,
        IReadOnlyList<EntityChipVm> soldItems,
        IReadOnlyList<EntityChipVm> quests,
        IReadOnlyList<NpcPreferenceRow> preferences,
        IReadOnlyList<string> giftSentimentTiers,
        EntityChipVm? areaChip = null,
        ICommand? openEntityCommand = null)
    {
        Npc = npc;
        InternalName = internalName;
        _nameResolver = nameResolver;
        Services = services;
        TaughtRecipes = taughtRecipes;
        TaughtAbilities = taughtAbilities;
        SoldItems = soldItems;
        Quests = quests;
        Preferences = preferences;
        GiftSentimentTiers = giftSentimentTiers;
        AreaChip = areaChip;
        OpenEntityCommand = openEntityCommand;

        // ── Phase 5 grammar-primitive projections ──────────────────────────────
        // Legacy chip/row members above stay (the existing tab tests + the
        // detail-pane contract); these are the grammar-tier carriers the view
        // binds. #404 Phase-2 classification for Npc:
        //   • Area chip = 1:1 ref = inline Prose Link;
        //   • Teaches/Sells/Quests chip clusters = 1:1 entity enumerations =
        //     Link Density="List";
        //   • Store-cap ItemKeyword detail chips = keyword filters ⇒
        //     Set-reference (ratified E4 — NOT Link);
        //   • the gold service-Type label = the named G-b "Npc service type"
        //     gold-in-Fact violation (dropped);
        //   • NPC InternalName footer = a cross-entity reference key (areas /
        //     quests resolve an NPC by it) ⇒ copyable KEY (Area's path).
        // NpcServiceRow / NpcServiceDetailLine are records in their own files
        // (outside this view's diff-guard scope), so they are WRAPPED here, not
        // edited — exactly the pilot's RecipeRequirementRow→RecipeRequirementRowVm
        // approach.

        AreaLink = AreaChip is null ? null : LinkVm.From(AreaChip);
        TaughtRecipeLinks = TaughtRecipes.Select(LinkVm.From).ToList();
        TaughtAbilityLinks = TaughtAbilities.Select(LinkVm.From).ToList();
        SoldItemLinks = SoldItems.Select(LinkVm.From).ToList();
        QuestLinks = Quests.Select(LinkVm.From).ToList();

        ServiceRowVms = Services
            .Select(s => new NpcServiceRowVm(
                s.Type,
                s.MinFavorTier,
                s.Details
                    .Select(d => new NpcServiceDetailLineVm(
                        d.Text,
                        d.Chips.Select(BuildFilterSetRef).ToList()))
                    .ToList()))
            .ToList();

        Footer = string.IsNullOrEmpty(InternalName)
            ? FactFooterVm.None()
            : FactFooterVm.Key(InternalName);
    }

    private NpcFilterSetRefVm BuildFilterSetRef(EntityChipVm chip)
    {
        // Store-cap keyword chips filter the Items tab ⇒ tag-form Set-ref
        // (ratified E4). Actionable iff the filter target is wired (navigable +
        // a host command); the per-chip Activate bridges SetRef's VM-param
        // click to OpenEntityCommand(reference) — the pilot RecipeKeywordSlotVm
        // idiom. Unwired ⇒ availability corollary (blue chassis, safe no-op).
        var wired = chip.IsNavigable && OpenEntityCommand is not null;
        var activate = wired
            ? new RelayCommand(() => OpenEntityCommand!.Execute(chip.Reference))
            : null;
        return new NpcFilterSetRefVm(
            new SetRefVm(chip.DisplayName, MatchCount: null, IsActionable: wired),
            activate);
    }

    public Npc Npc { get; }
    public string InternalName { get; }
    public string DisplayName => _nameResolver.Resolve(EntityRef.Npc(InternalName));

    /// <summary>
    /// Navigable chip to this NPC's home area (<see cref="EntityRef.Area(string)"/>). Null when
    /// the NPC has no <see cref="Npc.AreaName"/> set. Was a plain string (<c>AreaDisplayName</c>)
    /// before #245 shipped the Areas tab — the migration follows cookbook *Cross-link chips →
    /// audit existing surfaces*: once a kind ships, pre-existing call sites that surfaced its
    /// friendly name as plain text need to flip to <c>EntityChip</c> + <see cref="IReferenceNavigator.CanOpen"/>.
    /// </summary>
    public EntityChipVm? AreaChip { get; }
    public string? Pos => Npc.Pos;
    public string? Description => Npc.Desc;

    public IReadOnlyList<NpcServiceRow> Services { get; }
    public IReadOnlyList<EntityChipVm> TaughtRecipes { get; }
    public IReadOnlyList<EntityChipVm> TaughtAbilities { get; }
    public IReadOnlyList<EntityChipVm> SoldItems { get; }
    public IReadOnlyList<EntityChipVm> Quests { get; }
    public IReadOnlyList<NpcPreferenceRow> Preferences { get; }
    public IReadOnlyList<string> GiftSentimentTiers { get; }

    /// <summary>Comma-joined list of <see cref="GiftSentimentTiers"/> for the XAML <c>Run</c>.</summary>
    public string GiftSentimentTiersDisplay => string.Join(", ", GiftSentimentTiers);

    // ── Phase 5 grammar-primitive carriers ──────────────────────────────────

    /// <summary>Home-area cross-link as the unified <see cref="LinkVm"/> (inline
    /// Prose Link behind the Structure "Area:" prefix). Null when no area.</summary>
    public LinkVm? AreaLink { get; }

    /// <summary>Taught-recipe cross-links as <see cref="LinkVm"/> (Density="List").</summary>
    public IReadOnlyList<LinkVm> TaughtRecipeLinks { get; }

    /// <summary>Taught-ability cross-links as <see cref="LinkVm"/> (Density="List").</summary>
    public IReadOnlyList<LinkVm> TaughtAbilityLinks { get; }

    /// <summary>Sold-item cross-links as <see cref="LinkVm"/> (Density="List").</summary>
    public IReadOnlyList<LinkVm> SoldItemLinks { get; }

    /// <summary>Quest cross-links as <see cref="LinkVm"/> (Density="List"; G-c
    /// degrade keeps them identical at rest until the Quests tab ships).</summary>
    public IReadOnlyList<LinkVm> QuestLinks { get; }

    /// <summary>
    /// Services wrapped for the grammar: the gold Type label is dropped (named
    /// G-b "Npc service type" violation), the favor pill de-boxed, and each
    /// detail line's Store-cap ItemKeyword chips become tag-form Set-references
    /// (ratified E4). Wraps <see cref="NpcServiceRow"/> (out of edit scope).
    /// </summary>
    public IReadOnlyList<NpcServiceRowVm> ServiceRowVms { get; }

    /// <summary>
    /// Footer identifier strip (matrix #14 / G-a · ratified E5). The NPC
    /// InternalName is a cross-entity reference key (areas / quests resolve an
    /// NPC by it) ⇒ the copyable <c>KEY</c> (Area's path), not an inert
    /// envelope <c>ROW</c>. <see cref="FactFooterVm.None"/> if keyless.
    /// </summary>
    public FactFooterVm Footer { get; }

    /// <summary>
    /// Command invoked when the user clicks a cross-link chip (taught recipe / sold item).
    /// Receives the chip's <see cref="Mithril.Shared.Reference.EntityRef"/>. Wired by
    /// <see cref="NpcsTabViewModel"/> to the navigator.
    /// </summary>
    public ICommand? OpenEntityCommand { get; }
}

/// <summary>
/// View-side grammar wrapper of a <see cref="NpcServiceRow"/> (which lives in
/// its own file, outside this view's edit scope — wrapped, not mutated, exactly
/// like the pilot's <c>RecipeRequirementRowVm</c>). The view styles
/// <see cref="Type"/> as a Structure group header (the named G-b
/// "Npc service type" gold dropped) and <see cref="MinFavorTier"/> as inert
/// Fact (the bordered pill de-boxed).
/// </summary>
public sealed class NpcServiceRowVm
{
    public NpcServiceRowVm(string type, string? minFavorTier, IReadOnlyList<NpcServiceDetailLineVm> details)
    {
        Type = type;
        MinFavorTier = minFavorTier;
        Details = details;
    }

    public string Type { get; }
    public string? MinFavorTier { get; }
    public bool HasMinFavorTier => !string.IsNullOrEmpty(MinFavorTier);
    public IReadOnlyList<NpcServiceDetailLineVm> Details { get; }
}

/// <summary>One service detail line: prose <see cref="Text"/> (inert Fact) plus
/// the Store-cap keyword chips reshaped to tag-form Set-references (E4).</summary>
public sealed class NpcServiceDetailLineVm
{
    public NpcServiceDetailLineVm(string text, IReadOnlyList<NpcFilterSetRefVm> setRefs)
    {
        Text = text;
        SetRefs = setRefs;
    }

    public string Text { get; }
    public IReadOnlyList<NpcFilterSetRefVm> SetRefs { get; }
}

/// <summary>
/// A Set-reference carrier (the pilot <c>RecipeKeywordSlotVm</c> idiom): the
/// <see cref="SetRefVm"/> the shared <c>SetRef</c> binds plus the per-chip
/// <see cref="Activate"/> bridging <c>SetRef.ActivateCommand</c> (which passes
/// the VM) to the host <c>OpenEntityCommand(reference)</c>.
/// <see cref="Activate"/> is null for an unwired tag (availability corollary —
/// still the blue chassis; the click is a safe no-op).
/// </summary>
public sealed class NpcFilterSetRefVm
{
    public NpcFilterSetRefVm(SetRefVm setRef, ICommand? activate)
    {
        SetRef = setRef;
        Activate = activate;
    }

    public SetRefVm SetRef { get; }
    public ICommand? Activate { get; }
}
