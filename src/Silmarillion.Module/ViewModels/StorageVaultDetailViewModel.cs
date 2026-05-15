using System.Windows.Input;
using Mithril.Reference.Models.Misc;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using StorageVaultPoco = Mithril.Reference.Models.Misc.StorageVault;

namespace Silmarillion.ViewModels;

/// <summary>
/// StorageVault detail-pane view-model. Sections top-down:
/// <list type="number">
/// <item><b>Header</b> — <see cref="StorageVaultPoco.NpcFriendlyName"/> (large),
/// internal-name footer (the envelope key, mono small per the detail-view footer
/// convention).</item>
/// <item><b>Metadata row</b> — parent Area chip; operator NPC chip (only when
/// <see cref="StorageVaultPoco.HasAssociatedNpc"/> is true — false ⇒ a transfer chest with
/// no operator); an account-wide badge rendered only when true (noise-filter default —
/// every persistent badge carries information).</item>
/// <item><b>Capacity</b> — the favor-tier → slot-count table (<see cref="CapacityRows"/>)
/// when <see cref="StorageVaultPoco.Levels"/> is present, ordered by the canonical in-game
/// favor progression (NOT dict order); else the flat <see cref="FlatSlots"/>; the
/// script-atomic min–max range only when present; the rare event-gated
/// <see cref="StorageVaultPoco.EventLevels"/> rendered only when non-null, clearly
/// labeled.</item>
/// <item><b>Access requirements</b> — <see cref="RequirementDescription"/> prose, the
/// <see cref="ItemKeywordTags"/> plain tag cluster (item-keyword tags, NOT navigable
/// entities), and the polymorphic <see cref="Requirements"/> grouped by intent. An
/// <c>UnknownStorageRequirement</c> degrades to a noise-filtered "(unrecognised
/// requirement)" line so a future schema addition surfaces gracefully, not as a crash or
/// a blank.</item>
/// </list>
/// <para>
/// Every cross-link here is a 1:1 <see cref="EntityChipVm"/> (operator NPC, parent Area,
/// quest-completed requirement → Quest). There is no 1:N fan-out surface and therefore
/// no provenance popup / synthetic kind on this tab (#318 retired the synthetic-kind
/// family; "Vaults in this area" reverse-lookup is an Areas-tab concern, out of scope).
/// </para>
/// </summary>
public sealed class StorageVaultDetailViewModel
{
    /// <summary>
    /// Canonical in-game favor progression (mirrors <c>Arwen.Domain.FavorTier</c>, kept
    /// local to avoid a cross-module dependency). The favor table orders rows by this, NOT
    /// by the JSON dictionary's enumeration order. Any unrecognised future tier sorts last
    /// (alpha) so a PG patch surfaces visibly rather than silently.
    /// </summary>
    private static readonly string[] FavorOrder =
    {
        "Despised", "Hatred", "Disliked", "Tolerated", "Neutral", "Comfortable",
        "Friends", "CloseFriends", "BestFriends", "LikeFamily", "SoulMates",
    };

    public StorageVaultDetailViewModel(
        StorageVaultListRow row,
        IReferenceDataService refData,
        IReferenceNavigator navigator,
        IEntityNameResolver nameResolver,
        ICommand? openEntityCommand = null)
    {
        Row = row;
        OpenEntityCommand = openEntityCommand;
        var vault = row.Vault;
        DisplayName = row.DisplayName;
        EnvelopeKey = row.EnvelopeKey;
        IsAccountWide = row.IsAccountWide;
        Grouping = string.IsNullOrEmpty(vault.Grouping) ? null : vault.Grouping;

        AreaChip = BuildAreaChip(vault.Area, refData, nameResolver, navigator);

        // Operator NPC chip ONLY when the vault has an associated NPC. Account-wide
        // transfer chests (HasAssociatedNpc:false) have no operator — folding in an NPC
        // chip there would be a dead reference.
        OperatorNpcChip = vault.HasAssociatedNpc == true
            ? BuildNpcChip(row.EnvelopeKey, refData, nameResolver, navigator)
            : null;

        // ── Capacity ──
        CapacityRows = BuildCapacityRows(vault.Levels);
        HasCapacityTable = CapacityRows.Count > 0;
        FlatSlots = !HasCapacityTable && vault.NumSlots is { } n ? n : (int?)null;
        HasFlatSlots = FlatSlots is not null && FlatSlots.Value > 0;

        if (!string.IsNullOrEmpty(vault.NumSlotsScriptAtomic)
            && vault.NumSlotsScriptAtomicMinValue is { } lo
            && vault.NumSlotsScriptAtomicMaxValue is { } hi)
        {
            ScriptAtomicRange = $"Dynamic: {lo}–{hi} slots";
        }

        EventLevelRows = vault.EventLevels is { Count: > 0 } ev
            ? ev.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                 .Select(kv => new StorageVaultCapacityRow(kv.Key, kv.Value))
                 .ToList()
            : (IReadOnlyList<StorageVaultCapacityRow>)Array.Empty<StorageVaultCapacityRow>();
        HasEventLevels = EventLevelRows.Count > 0;

        // ── Access requirements ──
        RequirementDescription = string.IsNullOrEmpty(vault.RequirementDescription)
            ? null
            : vault.RequirementDescription;

        ItemKeywordTags = vault.RequiredItemKeywords is { Count: > 0 } kws
            ? kws.Where(k => !string.IsNullOrEmpty(k)).ToList()
            : (IReadOnlyList<string>)Array.Empty<string>();
        HasItemKeywordTags = ItemKeywordTags.Count > 0;

        var (reqLines, questChips) = BuildRequirements(vault.Requirements, refData, nameResolver, navigator);
        RequirementLines = reqLines;
        QuestRequirementChips = questChips;
        HasRequirementLines = RequirementLines.Count > 0;
        HasQuestRequirements = QuestRequirementChips.Count > 0;

        HasAccessRequirements =
            RequirementDescription is not null
            || HasItemKeywordTags
            || HasRequirementLines
            || HasQuestRequirements;
    }

    public StorageVaultListRow Row { get; }
    public string DisplayName { get; }

    /// <summary>
    /// Cross-link navigation command, supplied by the hosting tab. Bound by every
    /// <see cref="EntityChip"/> in the view (Area, operator NPC, quest-requirement).
    /// Null when constructed outside the tab (e.g. design-time / unit fixtures) — the
    /// chips then render as inert, which is the correct degrade.
    /// </summary>
    public ICommand? OpenEntityCommand { get; }

    /// <summary>
    /// Envelope key (operator NPC internal name, or a <c>"*"</c>-prefixed account-wide
    /// form) — rendered as the bottom-right monospace footer per the detail-view
    /// internal-name footer convention.
    /// </summary>
    public string EnvelopeKey { get; }

    /// <summary>True when the vault is account-wide (derived from the <c>"*"</c> prefix).</summary>
    public bool IsAccountWide { get; }

    /// <summary>Grouping facet (e.g. <c>"AreaSerbule"</c>), or null when absent.</summary>
    public string? Grouping { get; }
    public bool HasGrouping => Grouping is not null;

    // ── Metadata chips ──
    public EntityChipVm? AreaChip { get; }
    public bool HasArea => AreaChip is not null;

    /// <summary>Operator NPC chip, or null for a transfer chest (HasAssociatedNpc:false).</summary>
    public EntityChipVm? OperatorNpcChip { get; }
    public bool HasOperatorNpc => OperatorNpcChip is not null;

    // ── Capacity ──

    /// <summary>
    /// Favor-tier → slot-count rows, canonically ordered (NOT JSON dict order). Empty when
    /// the vault is not favor-scaled (then <see cref="FlatSlots"/> carries the count).
    /// </summary>
    public IReadOnlyList<StorageVaultCapacityRow> CapacityRows { get; }
    public bool HasCapacityTable { get; }

    public int? FlatSlots { get; }
    public bool HasFlatSlots { get; }

    /// <summary>"Dynamic: lo–hi slots" when the vault sizes via a server script atomic.</summary>
    public string? ScriptAtomicRange { get; }
    public bool HasScriptAtomicRange => ScriptAtomicRange is not null;

    /// <summary>Rare event-gated slot overrides (≈1 entry in the corpus); labeled distinctly.</summary>
    public IReadOnlyList<StorageVaultCapacityRow> EventLevelRows { get; }
    public bool HasEventLevels { get; }

    // ── Access requirements ──
    public string? RequirementDescription { get; }

    /// <summary>
    /// Item-keyword tags that gate what may be stored. These are item-keyword <i>tags</i>,
    /// NOT navigable entities (per the keyword-vs-item discrimination rule) — rendered as a
    /// plain tag cluster.
    /// </summary>
    public IReadOnlyList<string> ItemKeywordTags { get; }
    public bool HasItemKeywordTags { get; }

    /// <summary>
    /// Human-readable lines for the non-quest polymorphic requirements (interaction flag,
    /// server-rules flag, longtime-animal, warden, and the graceful "(unrecognised
    /// requirement)" degrade for <c>UnknownStorageRequirement</c>).
    /// </summary>
    public IReadOnlyList<string> RequirementLines { get; }
    public bool HasRequirementLines { get; }

    /// <summary>Quest-completed requirements as 1:1 navigable Quest chips.</summary>
    public IReadOnlyList<EntityChipVm> QuestRequirementChips { get; }
    public bool HasQuestRequirements { get; }

    public bool HasAccessRequirements { get; }

    // ── Helpers ──

    private static EntityChipVm? BuildAreaChip(
        string? areaKey,
        IReferenceDataService refData,
        IEntityNameResolver nameResolver,
        IReferenceNavigator navigator)
    {
        if (string.IsNullOrEmpty(areaKey)) return null;
        var reference = EntityRef.Area(areaKey);
        var displayName = refData.Areas.TryGetValue(areaKey, out var area)
            ? area.FriendlyName
            : nameResolver.Resolve(reference);
        return new EntityChipVm(
            DisplayName: displayName,
            IconId: 0,
            Reference: reference,
            IsNavigable: navigator.CanOpen(reference));
    }

    private static EntityChipVm BuildNpcChip(
        string envelopeKey,
        IReferenceDataService refData,
        IEntityNameResolver nameResolver,
        IReferenceNavigator navigator)
    {
        // The envelope key IS the operator NPC internal name when HasAssociatedNpc.
        var reference = EntityRef.Npc(envelopeKey);
        var displayName = refData.NpcsByInternalName.TryGetValue(envelopeKey, out var npc)
                          && !string.IsNullOrEmpty(npc.Name)
            ? npc.Name!
            : nameResolver.Resolve(reference);
        return new EntityChipVm(
            DisplayName: displayName,
            IconId: 0,
            Reference: reference,
            IsNavigable: navigator.CanOpen(reference));
    }

    private static IReadOnlyList<StorageVaultCapacityRow> BuildCapacityRows(
        IReadOnlyDictionary<string, int>? levels)
    {
        if (levels is null || levels.Count == 0)
            return Array.Empty<StorageVaultCapacityRow>();

        int Rank(string tier)
        {
            var i = Array.IndexOf(FavorOrder, tier);
            return i < 0 ? int.MaxValue : i;
        }

        return levels
            // A 0-slot favor tier (e.g. Despised:0, Comfortable:0) is the universal
            // default — no capacity granted yet. Drop it: every row should carry
            // information (cookbook *Default-value noise filtering*).
            .Where(kv => kv.Value > 0)
            .OrderBy(kv => Rank(kv.Key))
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new StorageVaultCapacityRow(SplitTier(kv.Key), kv.Value))
            .ToList();
    }

    /// <summary>"CloseFriends" → "Close Friends" (favor tiers are PascalCase in JSON).</summary>
    private static string SplitTier(string tier) =>
        System.Text.RegularExpressions.Regex.Replace(tier, "(?<=[a-z])([A-Z])", " $1");

    /// <summary>
    /// Renders the polymorphic <see cref="StorageRequirement"/> list grouped by intent
    /// (the quest-detail UX rule — reverse-lookup internal names to display, group by
    /// meaning, never mechanically dump subclass names). Quest-completed requirements
    /// become 1:1 navigable Quest chips; flags / identity gates become plain human labels;
    /// an <see cref="UnknownStorageRequirement"/> degrades to a single noise-filtered
    /// "(unrecognised requirement: …)" line so a future PG schema addition surfaces
    /// gracefully rather than crashing or rendering blank.
    /// </summary>
    private static (IReadOnlyList<string> Lines, IReadOnlyList<EntityChipVm> QuestChips) BuildRequirements(
        IReadOnlyList<StorageRequirement>? requirements,
        IReferenceDataService refData,
        IEntityNameResolver nameResolver,
        IReferenceNavigator navigator)
    {
        if (requirements is null || requirements.Count == 0)
            return (Array.Empty<string>(), Array.Empty<EntityChipVm>());

        var lines = new List<string>();
        var questChips = new List<EntityChipVm>();

        foreach (var req in requirements)
        {
            switch (req)
            {
                case StorageQuestCompletedRequirement q when !string.IsNullOrEmpty(q.Quest):
                {
                    var reference = EntityRef.Quest(q.Quest!);
                    questChips.Add(new EntityChipVm(
                        DisplayName: nameResolver.Resolve(reference),
                        IconId: 0,
                        Reference: reference,
                        IsNavigable: navigator.CanOpen(reference)));
                    break;
                }
                case StorageInteractionFlagSetRequirement f when !string.IsNullOrEmpty(f.InteractionFlag):
                    lines.Add($"Requires the interaction flag “{HumaniseFlag(f.InteractionFlag!)}”");
                    break;
                case StorageServerRulesFlagSetRequirement s when !string.IsNullOrEmpty(s.Flag):
                    lines.Add($"Requires the server-rules flag “{HumaniseFlag(s.Flag!)}”");
                    break;
                case StorageIsLongtimeAnimalRequirement:
                    lines.Add("Requires a long-time animal character");
                    break;
                case StorageIsWardenRequirement:
                    lines.Add("Requires Warden status");
                    break;
                case UnknownStorageRequirement u:
                    // Graceful degrade — a future PG-added discriminator. Surface the raw
                    // discriminator so it's diagnosable, but behind a clearly-noise-filtered
                    // label (never a crash, never a blank).
                    lines.Add(string.IsNullOrEmpty(u.DiscriminatorValue)
                        ? "(unrecognised requirement)"
                        : $"(unrecognised requirement: {u.DiscriminatorValue})");
                    break;
                default:
                    // A known subclass with an empty payload (defensive — shouldn't occur
                    // in the corpus). Skip silently rather than emit a blank line.
                    break;
            }
        }

        return (lines, questChips);
    }

    /// <summary>"Ivyn_Gave_Passcode" → "Ivyn Gave Passcode" — flags use '_' as a word sep.</summary>
    private static string HumaniseFlag(string flag) => flag.Replace('_', ' ');
}

/// <summary>
/// One capacity row: a favor-tier (or event) label and its granted slot count. Used both
/// for the favor table and the rare event-gated table.
/// </summary>
public sealed record StorageVaultCapacityRow(string Tier, int Slots);
