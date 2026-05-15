using System.Windows.Input;
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
/// and the per-type landmark section. The internal-name footer follows Mithril's
/// detail-view convention.
/// <para>
/// <b>#318 slice 4, surface 4 — two coupled 1:N surfaces routed through the shared
/// provenance popup:</b>
/// </para>
/// <list type="bullet">
/// <item>
/// <b>"NPCs in this area"</b> — the capped chip cluster <em>and</em> the full set are both
/// projected from <see cref="IReferenceDataService.NpcsByAreaWithReason"/> directly. The
/// "View all N →" affordance opens a <see cref="ProvenancePopupViewModel"/> fed that index;
/// there is no query re-derivation, so the popup count/membership cannot diverge from the
/// index (the #318 invariant). Replaces the retired <c>NpcByArea</c> synthetic-kind
/// ActionChip deep link. <b>Single-reason</b> relationship (an NPC is in an area iff its
/// AreaName matched — <see cref="NpcByAreaMatchReason.InArea"/>), so the popup collapses to
/// a flat list (#318 Discipline).
/// </item>
/// <item>
/// <b>Landmark groups</b> — the #311 fold-in. Landmarks render through the same shared
/// virtualizing <see cref="ProvenancePopupWindow"/>; there is no separate non-virtualized
/// list path. Landmark <c>Type</c> (Portal / MeditationPillar / TeleportationPlatform) is
/// genuine provenance — <em>which kind</em> of landmark it is — so this is a
/// <b>provenance-sectioned</b> popup (one section per Type), not a flat one. Landmarks
/// aren't navigable entities, so each row is a non-navigable <see cref="EntityChipVm"/>
/// with the Combo/Loc/Desc folded into the display label and a synthetic, distinct,
/// never-resolved <see cref="EntityRef"/> (so the popup's distinct-member count == the
/// landmark count and clicking is inert). The popup's recycling
/// <c>VirtualizingStackPanel</c> carries the ~547-row #259 precedent — high-cardinality
/// areas no longer render an unvirtualized list.
/// </item>
/// </list>
/// <para>
/// Group ordering is by gameplay relevance: Meditation Pillars first (the Combo readout is
/// the most-asked landmark question), then Portals (route-finding), then Teleportation
/// Platforms.
/// </para>
/// </summary>
public sealed partial class AreaDetailViewModel : ObservableObject
{
    /// <summary>
    /// Host-supplied opener for the two provenance popups ("NPCs in this area" and
    /// "Landmarks in this area"). Defaults to <see cref="ShowProvenancePopupWindow"/>
    /// (creates + <c>Show()</c>s a <see cref="ProvenancePopupWindow"/>). Tests swap in a
    /// capturing delegate so the VM is fully assertable without spawning a window. Opening
    /// the popup this way never calls <c>IReferenceNavigator</c>, so it pushes no
    /// back/forward history — identical non-navigating contract to
    /// <c>IReferenceKindTarget.TryOpenInWindow</c> and to surface 1's
    /// <c>ItemDetailViewModel.ProvenancePopupOpener</c>.
    /// </summary>
    public static Action<ProvenancePopupViewModel, ICommand?> ProvenancePopupOpener { get; set; }
        = ShowProvenancePopupWindow;

    private static void ShowProvenancePopupWindow(ProvenancePopupViewModel vm, ICommand? chipClick) =>
        new ProvenancePopupWindow { DataContext = vm, ChipClickCommand = chipClick }.Show();

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

        var (npcChips, npcsTotal, npcsPopup) = BuildNpcs(
            area.Key, refData, nameResolver, navigator, settings.UsedInChipCap);
        NpcChips = npcChips;
        NpcsTotal = npcsTotal;
        NpcsPopup = npcsPopup;
        ShowNpcsPopupCommand = new RelayCommand(
            () => ProvenancePopupOpener(NpcsPopup!, OpenEntityCommand),
            () => NpcsPopup is not null);

        var (landmarkGroups, landmarksTotal, landmarksPopup) = BuildLandmarks(area.Key, refData);
        LandmarkGroups = landmarkGroups;
        LandmarksTotal = landmarksTotal;
        LandmarksPopup = landmarksPopup;
        ShowLandmarksPopupCommand = new RelayCommand(
            () => ProvenancePopupOpener(LandmarksPopup!, OpenEntityCommand),
            () => LandmarksPopup is not null);
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

    // ── NPCs in this area ──────────────────────────────────────────────────────

    /// <summary>NPC chips for the "NPCs in this area" cluster, capped at <see cref="SilmarillionSettings.UsedInChipCap"/>.</summary>
    public IReadOnlyList<EntityChipVm> NpcChips { get; }

    /// <summary>
    /// Distinct count of NPCs in this area — equals
    /// <see cref="ProvenancePopupViewModel.TotalCount"/> of <see cref="NpcsPopup"/>.
    /// Drives the "View all N →" label. 0 ⇒ no NPCs and the whole section hides.
    /// </summary>
    public int NpcsTotal { get; }

    /// <summary>
    /// The "NPCs in this area" provenance popup VM opened by
    /// <see cref="ShowNpcsPopupCommand"/>, or <see langword="null"/> when no NPC lives in
    /// this area (#318 slice 4, surface 4). Built from
    /// <see cref="IReferenceDataService.NpcsByAreaWithReason"/> directly (membership +
    /// provenance), replacing the retired <c>NpcByArea</c> synthetic-kind deep link —
    /// there is no query re-derivation, so the displayed set cannot diverge from the
    /// index. Single-reason (<see cref="NpcByAreaMatchReason.InArea"/>) so the popup
    /// collapses to a flat list (#318 Discipline).
    /// </summary>
    public ProvenancePopupViewModel? NpcsPopup { get; }

    /// <summary>
    /// Opens <see cref="NpcsPopup"/> via <see cref="ProvenancePopupOpener"/>. Bound to the
    /// always-visible "View all N →" affordance. The popup is a window shown directly —
    /// opening it pushes no navigator history (#229 contract; mirrors surface 1's
    /// <c>ItemDetailViewModel.ShowConsumedByRecipesPopupCommand</c>).
    /// </summary>
    public ICommand ShowNpcsPopupCommand { get; }

    /// <summary>True when at least one NPC chip rendered (drives the section header visibility).</summary>
    public bool HasNpcs => NpcChips.Count > 0;

    // ── Landmarks in this area (#311 fold-in) ──────────────────────────────────

    /// <summary>
    /// Per-type landmark groups in render order (empty groups omitted). Drives the
    /// detail-pane preview cluster; the full set (and any high-cardinality area) is
    /// reachable via <see cref="ShowLandmarksPopupCommand"/> → the virtualizing
    /// <see cref="LandmarksPopup"/>.
    /// </summary>
    public IReadOnlyList<AreaLandmarkGroup> LandmarkGroups { get; }

    /// <summary>
    /// Total landmark count across all groups — equals
    /// <see cref="ProvenancePopupViewModel.TotalCount"/> of <see cref="LandmarksPopup"/>.
    /// 0 ⇒ no landmarks and the section hides.
    /// </summary>
    public int LandmarksTotal { get; }

    /// <summary>
    /// The "Landmarks in this area" provenance popup VM (#311 fold-in) opened by
    /// <see cref="ShowLandmarksPopupCommand"/>, or <see langword="null"/> when the area
    /// has no landmarks. Sectioned by landmark <c>Type</c> (Type is genuine provenance —
    /// which kind of landmark it is), so this is a multi-section popup that renders
    /// through the shared <see cref="ProvenancePopupWindow"/>'s recycling
    /// <c>VirtualizingStackPanel</c>. There is no separate non-virtualized list path for
    /// the full landmark set — #311's virtualization discipline is satisfied by the shared
    /// control.
    /// </summary>
    public ProvenancePopupViewModel? LandmarksPopup { get; }

    /// <summary>
    /// Opens <see cref="LandmarksPopup"/> via <see cref="ProvenancePopupOpener"/>. Same
    /// non-navigating contract as <see cref="ShowNpcsPopupCommand"/>.
    /// </summary>
    public ICommand ShowLandmarksPopupCommand { get; }

    /// <summary>True when at least one landmark group rendered (drives the section header visibility).</summary>
    public bool HasLandmarks => LandmarkGroups.Count > 0;

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build the "NPCs in this area" surface from
    /// <see cref="IReferenceDataService.NpcsByAreaWithReason"/> <b>directly</b>: the capped
    /// chip cluster (first <see cref="SilmarillionSettings.UsedInChipCap"/> by name)
    /// <em>and</em> the provenance popup VM. Both project from the <em>same</em>
    /// materialized index collection — no query re-derivation, so the popup's "View all N"
    /// count and membership cannot diverge from the index (the #318 invariant).
    /// Single-reason (<see cref="NpcByAreaMatchReason.InArea"/>) ⇒ one section ⇒
    /// <see cref="ProvenancePopupViewModel"/> renders a flat list (#318 Discipline).
    /// Returns <c>([], 0, null)</c> only when no NPC lives in the area.
    /// </summary>
    private static (IReadOnlyList<EntityChipVm> Chips, int Total, ProvenancePopupViewModel? Popup)
        BuildNpcs(
            string areaKey,
            IReferenceDataService refData,
            IEntityNameResolver nameResolver,
            IReferenceNavigator navigator,
            int cap)
    {
        if (!refData.NpcsByAreaWithReason.TryGetValue(areaKey, out var matches) || matches.Count == 0)
            return ([], 0, null);

        // Single materialization: order the index members once; both the popup and the
        // capped cluster are views over this exact list.
        var ordered = matches
            .Where(m => !string.IsNullOrEmpty(m.Npc.Key))
            .OrderBy(m => string.IsNullOrEmpty(m.Npc.Name) ? m.Npc.Key : m.Npc.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ordered.Count == 0)
            return ([], 0, null);

        EntityChipVm Chip(NpcEntry npc)
        {
            var reference = EntityRef.Npc(npc.Key);
            return new EntityChipVm(
                DisplayName: nameResolver.Resolve(reference),
                IconId: 0,
                Reference: reference,
                IsNavigable: navigator.CanOpen(reference));
        }

        var allChips = ordered.Select(m => Chip(m.Npc)).ToList();
        var capped = cap == 0
            ? (IReadOnlyList<EntityChipVm>)Array.Empty<EntityChipVm>()
            : allChips.Take(cap).ToList();

        // Single-reason ⇒ exactly one section ⇒ ProvenancePopupViewModel renders a flat
        // list (no reason header). ToQueryCommand intentionally unset (mirrors the slice-2
        // / surface-1 decision): the popup-from-index is the count-bearing surface; the
        // labeled-lossy To-Query projection is a deliberate fast-follow.
        var areaName = refData.Areas.TryGetValue(areaKey, out var ae) ? ae.FriendlyName : areaKey;
        var popup = new ProvenancePopupViewModel(
            title: $"NPCs in {areaName}",
            sections: new List<ProvenancePopupSection>
            {
                new("NPCs in this area", allChips),
            });

        return (capped, ordered.Count, popup);
    }

    /// <summary>
    /// Build the "Landmarks in this area" surface (#311 fold-in). Produces the per-type
    /// preview groups (the in-pane cluster) <em>and</em> a provenance-sectioned popup VM
    /// (one section per landmark <c>Type</c>) — both views over the same single
    /// materialization of <see cref="IReferenceDataService.Landmarks"/> for the area.
    /// Landmark <c>Type</c> is genuine provenance, so the popup is sectioned, never flat.
    /// Landmarks aren't navigable entities: each popup row is a non-navigable
    /// <see cref="EntityChipVm"/> whose label folds in the Combo/Loc/Desc and whose
    /// <see cref="EntityRef"/> is synthetic + distinct per landmark (so the popup's
    /// distinct-member <see cref="ProvenancePopupViewModel.TotalCount"/> equals the
    /// landmark count and clicking is inert). Returns <c>([], 0, null)</c> when the area
    /// has no landmarks.
    /// </summary>
    private static (IReadOnlyList<AreaLandmarkGroup> Groups, int Total, ProvenancePopupViewModel? Popup)
        BuildLandmarks(string areaKey, IReferenceDataService refData)
    {
        if (!refData.Landmarks.TryGetValue(areaKey, out var landmarks) || landmarks.Count == 0)
            return ([], 0, null);

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
        var sections = new List<ProvenancePopupSection>(byType.Count);

        void Emit(string type, List<PocoLandmark> list)
        {
            var (group, section) = BuildGroupAndSection(areaKey, type, list);
            groups.Add(group);
            sections.Add(section);
        }

        foreach (var type in orderedTypes)
        {
            if (!byType.TryGetValue(type, out var list)) continue;
            Emit(type, list);
            byType.Remove(type);
        }
        // Any leftover (unexpected) types append in alpha order so a future PG patch surfaces
        // visibly rather than silently.
        foreach (var (type, list) in byType.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            Emit(type, list);

        var total = landmarks.Count;
        // Sectioned popup: landmark Type IS the provenance ("which kind of landmark").
        // Routed through the shared virtualizing ProvenancePopupWindow (#311) — there is
        // no separate non-virtualized list path for the full set anymore.
        var areaName = refData.Areas.TryGetValue(areaKey, out var ae) ? ae.FriendlyName : areaKey;
        var popup = new ProvenancePopupViewModel(
            title: $"Landmarks in {areaName}",
            sections: sections);

        return (groups, total, popup);
    }

    private static (AreaLandmarkGroup Group, ProvenancePopupSection Section) BuildGroupAndSection(
        string areaKey, string type, IReadOnlyList<PocoLandmark> entries)
    {
        var label = type switch
        {
            "MeditationPillar" => "Meditation Pillars",
            "Portal" => "Portals",
            "TeleportationPlatform" => "Teleportation Platforms",
            _ => type,
        };
        var heading = $"{label} ({entries.Count})";

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

        // Popup chips: non-navigable, distinct-per-landmark synthetic ref so
        // ProvenancePopupViewModel.TotalCount == landmark count and clicking is inert
        // (EntityChip's click handler early-returns on !IsNavigable). The rich Combo/Loc/
        // Desc detail is folded into the chip label since landmarks have no detail view.
        var chips = new List<EntityChipVm>(ordered.Count);
        var idx = 0;
        foreach (var e in ordered)
        {
            var name = e.Name ?? "";
            var detail = type switch
            {
                "MeditationPillar" => Join(
                    string.IsNullOrEmpty(e.Combo) ? null : $"Combo: {e.Combo}",
                    string.IsNullOrEmpty(e.Loc) ? null : e.Loc),
                "Portal" => Join(
                    string.IsNullOrEmpty(e.Desc) ? null : e.Desc,
                    string.IsNullOrEmpty(e.Loc) ? null : e.Loc),
                _ => string.IsNullOrEmpty(e.Loc) ? null : e.Loc,
            };
            var display = string.IsNullOrEmpty(detail) ? name : $"{name} · {detail}";
            // Synthetic, distinct, never-resolved reference. Area kind keeps it in a real
            // enum value (no synthetic EntityKind — #318 deletes those), the '#landmark:'
            // payload guarantees the navigator never matches it and the index is distinct
            // per landmark so the popup count is correct.
            var syntheticRef = EntityRef.Area($"{areaKey}#landmark:{type}:{idx}:{name}");
            chips.Add(new EntityChipVm(
                DisplayName: display,
                IconId: 0,
                Reference: syntheticRef,
                IsNavigable: false));
            idx++;
        }

        return (new AreaLandmarkGroup(type, heading, rows), new ProvenancePopupSection(label, chips));

        static string? Join(string? a, string? b) =>
            (a, b) switch
            {
                (null, null) => null,
                (not null, null) => a,
                (null, not null) => b,
                _ => $"{a} · {b}",
            };
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
