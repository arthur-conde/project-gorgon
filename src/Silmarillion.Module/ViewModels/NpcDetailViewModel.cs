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
        IReadOnlyList<EntityChipVm> soldItems,
        IReadOnlyList<NpcQuestLink> quests,
        IReadOnlyList<NpcPreferenceRow> preferences,
        IReadOnlyList<string> giftSentimentTiers,
        ICommand? openEntityCommand = null)
    {
        Npc = npc;
        InternalName = internalName;
        _nameResolver = nameResolver;
        Services = services;
        TaughtRecipes = taughtRecipes;
        SoldItems = soldItems;
        Quests = quests;
        Preferences = preferences;
        GiftSentimentTiers = giftSentimentTiers;
        OpenEntityCommand = openEntityCommand;
    }

    public Npc Npc { get; }
    public string InternalName { get; }
    public string DisplayName => _nameResolver.Resolve(EntityRef.Npc(InternalName));
    public string? AreaDisplayName => Npc.AreaFriendlyName ?? Npc.AreaName;
    public string? Pos => Npc.Pos;
    public string? Description => Npc.Desc;

    public IReadOnlyList<NpcServiceRow> Services { get; }
    public IReadOnlyList<EntityChipVm> TaughtRecipes { get; }
    public IReadOnlyList<EntityChipVm> SoldItems { get; }
    public IReadOnlyList<NpcQuestLink> Quests { get; }
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
