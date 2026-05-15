using System.Windows.Input;
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

    /// <summary>
    /// Command invoked when the user clicks a cross-link chip (taught recipe / sold item).
    /// Receives the chip's <see cref="Mithril.Shared.Reference.EntityRef"/>. Wired by
    /// <see cref="NpcsTabViewModel"/> to the navigator.
    /// </summary>
    public ICommand? OpenEntityCommand { get; }
}
