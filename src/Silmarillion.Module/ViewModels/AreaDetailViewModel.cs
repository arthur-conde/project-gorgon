using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Reference;

namespace Silmarillion.ViewModels;

/// <summary>
/// Area detail-pane view-model. Skeleton for slice (c) — surfaces FriendlyName,
/// ShortFriendlyName (suppressed when equal to FriendlyName), and the InternalName
/// footer per the Mithril detail-view convention. NPC cluster and landmark section
/// land in slice (d) (#245).
/// </summary>
public sealed partial class AreaDetailViewModel : ObservableObject
{
    public AreaDetailViewModel(
        AreaEntry area,
        IReferenceDataService refData,
        IReferenceNavigator navigator,
        IEntityNameResolver nameResolver,
        Silmarillion.SilmarillionSettings settings,
        RelayCommand<EntityRef?> openEntityCommand)
    {
        Area = area;
        DisplayName = area.FriendlyName;
        ShortFriendlyName = string.Equals(area.FriendlyName, area.ShortFriendlyName, StringComparison.Ordinal)
            ? null
            : area.ShortFriendlyName;
        InternalName = area.Key;
    }

    public AreaEntry Area { get; }
    public string DisplayName { get; }

    /// <summary>
    /// Nulled-out when identical to <see cref="DisplayName"/> (cookbook *Default-value
    /// noise filtering*) — every populated chip / row carries information.
    /// </summary>
    public string? ShortFriendlyName { get; }

    /// <summary>
    /// Area envelope key (e.g. <c>"AreaSerbule"</c>) — rendered as the bottom-right
    /// monospace footer per Mithril's detail-view internal-name footer convention.
    /// </summary>
    public string InternalName { get; }
}
