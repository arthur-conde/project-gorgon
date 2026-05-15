using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using PocoLandmark = Mithril.Reference.Models.Misc.Landmark;

namespace Silmarillion.ViewModels;

/// <summary>
/// Area detail-pane view-model. Surfaces FriendlyName + ShortFriendlyName header (the latter
/// nulled when equal to FriendlyName per cookbook *Default-value noise filtering*), the
/// "NPCs in this area" chip cluster capped at <see cref="SilmarillionSettings.UsedInChipCap"/>,
/// and the per-type landmark sections (Portal / MeditationPillar / TeleportationPlatform —
/// empty groups hidden). The internal-name footer follows Mithril's detail-view convention.
/// <para>
/// Group ordering is by gameplay relevance: Meditation Pillars first (the Combo readout is
/// the most-asked landmark question), then Portals (route-finding), then Teleportation
/// Platforms. Picked during real-data walk; flagged as a PR open question alongside the
/// NPC cluster cap.
/// </para>
/// <para>
/// Landmark rows are split into three record types (<see cref="AreaLandmarkPortalRow"/>,
/// <see cref="AreaLandmarkPillarRow"/>, <see cref="AreaLandmarkPlatformRow"/>) — per cookbook
/// guidance on polymorphic sub-list shapes, each ships its own DataTemplate rather than a
/// shared layout with Style.Triggers. Mixed font runs (bold name, italic desc, mono loc/combo)
/// are easier to express per-template than per-Type DataTriggers on a single row.
/// </para>
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
        OpenEntityCommand = openEntityCommand;

        var (chips, shortcut) = BuildNpcChips(area.Key, refData, nameResolver, navigator, settings.UsedInChipCap);
        NpcChips = chips;
        NpcsTabShortcut = shortcut;

        LandmarkGroups = BuildLandmarkGroups(area.Key, refData);
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

    public RelayCommand<EntityRef?> OpenEntityCommand { get; }

    /// <summary>NPC chips for the "NPCs in this area" cluster, capped at <see cref="SilmarillionSettings.UsedInChipCap"/>.</summary>
    public IReadOnlyList<EntityChipVm> NpcChips { get; }

    /// <summary>
    /// Always-visible shortcut chip below <see cref="NpcChips"/> that deep-links into the
    /// NPCs tab filtered by <c>AreaName = "&lt;areaKey&gt;"</c> (anchored on
    /// <see cref="EntityRef.NpcByArea(string)"/>). Carries the total NPC count, so when
    /// the chip strip is capped the user still sees the full size and can jump to the
    /// master-list context for sorting / further filtering. <see langword="null"/> only
    /// when no NPCs live in this area (the chip would be meaningless then).
    /// <para>
    /// Departs from cookbook *cap + overflow pill* (pill only on overflow): for areas the
    /// shortcut has value at any cardinality — switching to the NPCs tab lets the user
    /// sort by service type, narrow by gift preference, etc.
    /// </para>
    /// </summary>
    public EntityChipVm? NpcsTabShortcut { get; }

    /// <summary>True when at least one NPC chip rendered (drives the section header visibility).</summary>
    public bool HasNpcs => NpcChips.Count > 0;

    /// <summary>Per-type landmark groups in render order (empty groups omitted).</summary>
    public IReadOnlyList<AreaLandmarkGroup> LandmarkGroups { get; }

    /// <summary>True when at least one landmark group rendered (drives the section header visibility).</summary>
    public bool HasLandmarks => LandmarkGroups.Count > 0;

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static (IReadOnlyList<EntityChipVm> Chips, EntityChipVm? Shortcut) BuildNpcChips(
        string areaKey,
        IReferenceDataService refData,
        IEntityNameResolver nameResolver,
        IReferenceNavigator navigator,
        int cap)
    {
        if (!refData.NpcsByArea.TryGetValue(areaKey, out var npcs) || npcs.Count == 0)
            return ([], null);

        var ordered = npcs
            .Where(n => !string.IsNullOrEmpty(n.Key))
            .OrderBy(n => string.IsNullOrEmpty(n.Name) ? n.Key : n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var visibleCount = Math.Min(cap, ordered.Count);
        var chips = new List<EntityChipVm>(visibleCount);
        for (var i = 0; i < visibleCount; i++)
        {
            var npc = ordered[i];
            var reference = EntityRef.Npc(npc.Key);
            chips.Add(new EntityChipVm(
                DisplayName: nameResolver.Resolve(reference),
                IconId: 0,
                Reference: reference,
                IsNavigable: navigator.CanOpen(reference)));
        }

        // Always-visible shortcut into the NPCs tab filtered by area. Label includes the
        // total NPC count so the user can tell at a glance whether the chip strip is
        // showing everything or just the cap; when N ≤ cap the chip is still useful for
        // jumping to the master-list view (sortable by service type, gift prefs, etc.).
        var shortcutReference = EntityRef.NpcByArea(areaKey);
        var shortcut = new EntityChipVm(
            DisplayName: $"View all {ordered.Count} in NPCs tab →",
            IconId: 0,
            Reference: shortcutReference,
            IsNavigable: navigator.CanOpen(shortcutReference));
        return (chips, shortcut);
    }

    private static IReadOnlyList<AreaLandmarkGroup> BuildLandmarkGroups(
        string areaKey,
        IReferenceDataService refData)
    {
        if (!refData.Landmarks.TryGetValue(areaKey, out var landmarks) || landmarks.Count == 0)
            return [];

        var byType = new Dictionary<string, List<PocoLandmark>>(StringComparer.Ordinal);
        foreach (var lm in landmarks)
        {
            var type = string.IsNullOrEmpty(lm.Type) ? "(unknown)" : lm.Type!;
            if (!byType.TryGetValue(type, out var list))
            {
                list = new List<PocoLandmark>();
                byType[type] = list;
            }
            list.Add(lm);
        }

        // Gameplay-relevance order: Meditation pillars (combo readout is the most-asked landmark
        // question), then Portals (route-finding), then Teleportation Platforms. Any unknown
        // type sorts last alphabetically — defensive: the bundled corpus only carries those three.
        var orderedTypes = new[] { "MeditationPillar", "Portal", "TeleportationPlatform" };
        var groups = new List<AreaLandmarkGroup>(byType.Count);
        foreach (var type in orderedTypes)
        {
            if (!byType.TryGetValue(type, out var list)) continue;
            groups.Add(BuildGroup(type, list));
            byType.Remove(type);
        }
        // Any leftover (unexpected) types append in alpha order so a future PG patch surfaces
        // visibly rather than silently.
        foreach (var (type, list) in byType.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            groups.Add(BuildGroup(type, list));
        }
        return groups;
    }

    private static AreaLandmarkGroup BuildGroup(string type, IReadOnlyList<PocoLandmark> entries)
    {
        var heading = type switch
        {
            "MeditationPillar" => $"Meditation Pillars ({entries.Count})",
            "Portal" => $"Portals ({entries.Count})",
            "TeleportationPlatform" => $"Teleportation Platforms ({entries.Count})",
            _ => $"{type} ({entries.Count})",
        };

        var ordered = entries
            .OrderBy(e => string.IsNullOrEmpty(e.Name) ? "" : e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = type switch
        {
            "MeditationPillar" => (IReadOnlyList<IAreaLandmarkRow>)ordered
                .Select(e => (IAreaLandmarkRow)new AreaLandmarkPillarRow(
                    e.Name ?? "",
                    string.IsNullOrEmpty(e.Combo) ? null : e.Combo,
                    string.IsNullOrEmpty(e.Loc) ? null : e.Loc))
                .ToList(),
            "Portal" => ordered
                .Select(e => (IAreaLandmarkRow)new AreaLandmarkPortalRow(
                    e.Name ?? "",
                    string.IsNullOrEmpty(e.Desc) ? null : e.Desc,
                    string.IsNullOrEmpty(e.Loc) ? null : e.Loc))
                .ToList(),
            "TeleportationPlatform" => ordered
                .Select(e => (IAreaLandmarkRow)new AreaLandmarkPlatformRow(
                    e.Name ?? "",
                    string.IsNullOrEmpty(e.Loc) ? null : e.Loc))
                .ToList(),
            _ => ordered
                .Select(e => (IAreaLandmarkRow)new AreaLandmarkPortalRow(
                    e.Name ?? "",
                    string.IsNullOrEmpty(e.Desc) ? null : e.Desc,
                    string.IsNullOrEmpty(e.Loc) ? null : e.Loc))
                .ToList(),
        };

        return new AreaLandmarkGroup(type, heading, rows);
    }
}

/// <summary>
/// One per-Type group rendered on Area detail. <paramref name="Type"/> is the raw key
/// (<c>"Portal"</c> / <c>"MeditationPillar"</c> / <c>"TeleportationPlatform"</c>);
/// <paramref name="Heading"/> is the pre-formatted small-caps section header
/// (<c>"Portals (180)"</c>). <paramref name="Rows"/> carries the per-type row records —
/// XAML's DataType-keyed DataTemplates pick the right layout.
/// </summary>
public sealed record AreaLandmarkGroup(string Type, string Heading, IReadOnlyList<IAreaLandmarkRow> Rows);

/// <summary>Marker interface for the per-type landmark row records.</summary>
public interface IAreaLandmarkRow
{
    string Name { get; }
}

/// <summary>
/// One portal row. The bundled corpus carries 180 portals — the <see cref="Desc"/>
/// (e.g. <c>"Return to Statehelm"</c>) is the gameplay-relevant flavor; <see cref="Loc"/>
/// is the coordinate string for surveyors / cartographers.
/// </summary>
public sealed record AreaLandmarkPortalRow(string Name, string? Desc, string? Loc) : IAreaLandmarkRow
{
    /// <summary>" — <desc>" or empty when no Desc.</summary>
    public string DescDisplay => string.IsNullOrEmpty(Desc) ? "" : $" — {Desc}";

    /// <summary>" · <loc>" or empty when no Loc.</summary>
    public string LocDisplay => string.IsNullOrEmpty(Loc) ? "" : $" · {Loc}";
}

/// <summary>
/// One meditation pillar row. The bundled corpus carries 55 pillars; the
/// <see cref="Combo"/> (four-digit code) is the most-asked landmark readout in-game.
/// XAML's per-template binding handles each field separately so the Combo and Loc fields
/// can render in mono.
/// </summary>
public sealed record AreaLandmarkPillarRow(string Name, string? Combo, string? Loc) : IAreaLandmarkRow
{
    /// <summary>" · Combo: <combo>" or empty when no Combo.</summary>
    public string ComboDisplay => string.IsNullOrEmpty(Combo) ? "" : $" · Combo: {Combo}";

    /// <summary>" · <loc>" or empty when no Loc.</summary>
    public string LocDisplay => string.IsNullOrEmpty(Loc) ? "" : $" · {Loc}";
}

/// <summary>One teleportation platform row. Desc is typically empty on the bundled corpus.</summary>
public sealed record AreaLandmarkPlatformRow(string Name, string? Loc) : IAreaLandmarkRow
{
    /// <summary>" · <loc>" or empty when no Loc.</summary>
    public string LocDisplay => string.IsNullOrEmpty(Loc) ? "" : $" · {Loc}";
}
