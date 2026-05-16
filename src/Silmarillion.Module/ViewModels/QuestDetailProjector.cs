using System.Globalization;
using Mithril.Reference.Models.Quests;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;

namespace Silmarillion.ViewModels;

/// <summary>
/// Projects a <see cref="Quest"/> POCO to the player-facing display rows used by
/// <see cref="QuestDetailViewModel"/>: requirement groups (intent-bucketed, internal-names
/// resolved), reward groups, objective rows, and the cross-link chip collections (item /
/// recipe / quest follow-ups, NPC giver/turn-in).
/// <para>
/// The polymorphic <see cref="QuestRequirement"/> hierarchy (42 concrete subclasses) and
/// <see cref="QuestReward"/> hierarchy (9 concrete subclasses) are bucketed by what the
/// player needs to understand (a skill gate, a story prerequisite, a moon phase…) rather
/// than by their raw discriminator <c>T</c> value. This is the "group by intent, not by
/// class" rule from the cookbook — the NPC-Training cautionary tale was rendering 12
/// subclass fields in one flat bullet list and being unscannable.
/// </para>
/// </summary>
public static class QuestDetailProjector
{
    // ─── Requirements ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Bucket a quest's flat <see cref="Quest.Requirements"/> into intent-labelled groups.
    /// Empty groups drop out of the result. Internal-name references inside each subclass
    /// (skills, NPCs, items, quests, abilities) get resolved through the entity-name
    /// resolver / reference-data dictionaries so the rendered text is player-readable.
    /// </summary>
    public static IReadOnlyList<QuestRequirementGroup> BuildRequirementGroups(
        IReadOnlyList<QuestRequirement>? requirements,
        IReferenceDataService refData,
        IEntityNameResolver resolver,
        IReferenceNavigator navigator)
    {
        if (requirements is null || requirements.Count == 0)
            return Array.Empty<QuestRequirementGroup>();

        var buckets = new Dictionary<RequirementBucket, List<QuestRequirementDisplay>>();
        foreach (var req in requirements)
        {
            var bucket = ClassifyRequirement(req);
            var display = ProjectRequirement(req, refData, resolver, navigator);
            if (display is null) continue;
            if (!buckets.TryGetValue(bucket, out var list))
            {
                list = [];
                buckets[bucket] = list;
            }
            list.Add(display);
        }

        // Emit groups in player-relevance order — story prerequisites read first because
        // a "you must have finished X" gate is the most concrete; internal flags read last
        // because they're developer-facing.
        var order = new[]
        {
            RequirementBucket.StoryPrerequisites,
            RequirementBucket.SkillLevel,
            RequirementBucket.Favor,
            RequirementBucket.Identity,
            RequirementBucket.Inventory,
            RequirementBucket.TimeAndMoon,
            RequirementBucket.LocationAndArea,
            RequirementBucket.CombatState,
            RequirementBucket.Pets,
            RequirementBucket.Composite,
            RequirementBucket.InternalFlags,
            RequirementBucket.Unrecognised,
        };

        var groups = new List<QuestRequirementGroup>();
        foreach (var bucket in order)
        {
            if (!buckets.TryGetValue(bucket, out var list) || list.Count == 0) continue;
            groups.Add(new QuestRequirementGroup(LabelFor(bucket), list));
        }
        return groups;
    }

    private enum RequirementBucket
    {
        StoryPrerequisites,
        SkillLevel,
        Favor,
        Identity,
        Inventory,
        TimeAndMoon,
        LocationAndArea,
        CombatState,
        Pets,
        Composite,
        InternalFlags,
        Unrecognised,
    }

    private static string LabelFor(RequirementBucket bucket) => bucket switch
    {
        RequirementBucket.StoryPrerequisites => "Story prerequisites",
        RequirementBucket.SkillLevel => "Skill / ability gates",
        RequirementBucket.Favor => "Favor",
        RequirementBucket.Identity => "Identity / state",
        RequirementBucket.Inventory => "Inventory & equipment",
        RequirementBucket.TimeAndMoon => "Time & moon",
        RequirementBucket.LocationAndArea => "Location & area",
        RequirementBucket.CombatState => "Combat state",
        RequirementBucket.Pets => "Pets",
        RequirementBucket.Composite => "Any of",
        RequirementBucket.InternalFlags => "Internal flags",
        RequirementBucket.Unrecognised => "Unrecognised (data drift)",
        _ => "Other",
    };

    private static RequirementBucket ClassifyRequirement(QuestRequirement req) => req switch
    {
        QuestCompletedRequirement or QuestCompletedRecentlyRequirement
            or HangOutCompletedRequirement or GuildQuestCompletedRequirement => RequirementBucket.StoryPrerequisites,

        MinSkillLevelRequirement or MinCombatSkillLevelRequirement
            or ActiveCombatSkillRequirement or AbilityKnownRequirement => RequirementBucket.SkillLevel,

        MinFavorLevelRequirement or MinFavorRequirement => RequirementBucket.Favor,

        RaceRequirement or IsVampireRequirement or IsWardenRequirement
            or IsLongtimeAnimalRequirement or IsNotGuestRequirement
            or AppearanceRequirement => RequirementBucket.Identity,

        InventoryItemRequirement or EquipmentSlotEmptyRequirement
            or EquippedItemKeywordRequirement or HasMountInStableRequirement
            or HasEffectKeywordRequirement => RequirementBucket.Inventory,

        TimeOfDayRequirement or MoonPhaseRequirement
            or FullMoonRequirement or DayOfWeekRequirement => RequirementBucket.TimeAndMoon,

        AreaEventOnRequirement or AreaEventOffRequirement
            or InHotspotRequirement or MonsterTargetLevelRequirement
            or OtherHasTypeTagRequirement => RequirementBucket.LocationAndArea,

        InCombatRequirement or InCombatWithEliteRequirement => RequirementBucket.CombatState,

        PetCountRequirement => RequirementBucket.Pets,

        OrRequirement => RequirementBucket.Composite,

        UnknownQuestRequirement => RequirementBucket.Unrecognised,

        // Flag / script atomic / runtime-behavior / shape / physical-state — all developer-
        // facing knobs the player wouldn't recognise. Keep them visible (drift detection)
        // but cluster them at the bottom of the pane.
        _ => RequirementBucket.InternalFlags,
    };

    private static QuestRequirementDisplay? ProjectRequirement(
        QuestRequirement req,
        IReferenceDataService refData,
        IEntityNameResolver resolver,
        IReferenceNavigator navigator)
    {
        switch (req)
        {
            case MinSkillLevelRequirement m:
                return new QuestRequirementDisplay($"{ResolveSkillName(refData, m.Skill)} {m.Level}", null, false);

            case MinCombatSkillLevelRequirement m:
                return new QuestRequirementDisplay($"Combat level {m.Level}", null, false);

            case ActiveCombatSkillRequirement m:
                {
                    var skill = ResolveSkillName(refData, m.Skill);
                    return new QuestRequirementDisplay(
                        string.IsNullOrEmpty(m.AllowSkill) ? $"Active combat skill: {skill}" : $"Active combat skill: {skill} or {ResolveSkillName(refData, m.AllowSkill)}",
                        null, false);
                }

            case AbilityKnownRequirement m:
                // Abilities aren't tabbed yet — render the resolved string (we don't have a
                // friendly-name index for abilities exposed here today, fall back to the
                // ability internal name with camel-case split).
                return new QuestRequirementDisplay($"Knows ability: {SplitCamelCase(m.Ability)}", null, false);

            case MinFavorLevelRequirement m:
                {
                    var chipName = ResolveNpcDisplayWithArea(refData, resolver, m.Npc);
                    var reference = BuildNpcRef(m.Npc, navigator, out var canOpenNpc);
                    var prefix = $"{m.Level} with";
                    return new QuestRequirementDisplay(
                        Text: $"{prefix} {chipName}",
                        Reference: reference,
                        IsNavigable: canOpenNpc,
                        Prefix: prefix,
                        ChipName: chipName);
                }

            case MinFavorRequirement m:
                {
                    var chipName = ResolveNpcDisplayWithArea(refData, resolver, m.Npc);
                    var reference = BuildNpcRef(m.Npc, navigator, out var canOpenNpc2);
                    var prefix = $"{m.MinFavor:N0} favor with";
                    return new QuestRequirementDisplay(
                        Text: $"{prefix} {chipName}",
                        Reference: reference,
                        IsNavigable: canOpenNpc2,
                        Prefix: prefix,
                        ChipName: chipName);
                }

            case QuestCompletedRequirement q:
                return BuildQuestChip(q.Quest, "Completed:", resolver, navigator);

            case QuestCompletedRecentlyRequirement q:
                return BuildQuestChip(q.Quest, "Completed recently:", resolver, navigator);

            case HangOutCompletedRequirement h:
                return new QuestRequirementDisplay($"Hangout completed: {SplitCamelCase(h.HangOut)}", null, false);

            case GuildQuestCompletedRequirement g:
                return BuildQuestChip(g.Quest, "Guild quest completed:", resolver, navigator);

            case RaceRequirement r:
                if (!string.IsNullOrEmpty(r.AllowedRace))
                    return new QuestRequirementDisplay($"Must be a {SplitCamelCase(r.AllowedRace)}", null, false);
                if (!string.IsNullOrEmpty(r.DisallowedRace))
                    return new QuestRequirementDisplay($"Must NOT be a {SplitCamelCase(r.DisallowedRace)}", null, false);
                return null;

            case IsVampireRequirement: return new QuestRequirementDisplay("Must be a vampire", null, false);
            case IsWardenRequirement: return new QuestRequirementDisplay("Must be a warden", null, false);
            case IsLongtimeAnimalRequirement: return new QuestRequirementDisplay("Must be a longtime animal-form character", null, false);
            case IsNotGuestRequirement: return new QuestRequirementDisplay("Must be a full account (not guest)", null, false);

            case AppearanceRequirement a:
                return new QuestRequirementDisplay($"Appearance: {SplitCamelCase(a.Appearance)}", null, false);

            case InventoryItemRequirement i:
                return BuildItemChip(i.Item, "Has", resolver, refData, navigator);

            case EquipmentSlotEmptyRequirement e:
                return new QuestRequirementDisplay($"{SplitCamelCase(e.Slot)} slot empty", null, false);

            case EquippedItemKeywordRequirement e:
                return new QuestRequirementDisplay($"Equipped: {SplitCamelCase(e.Keyword)}", null, false);

            case HasMountInStableRequirement h:
                return new QuestRequirementDisplay(
                    h.MinimumMountsNeeded is > 1
                        ? $"Has {h.MinimumMountsNeeded} mounts stabled"
                        : "Has a mount stabled",
                    null, false);

            case HasEffectKeywordRequirement h:
                {
                    // A "Has effect keyword X" requirement is a predicate over effect keywords,
                    // not a single effect row — many effects may carry the same keyword. Anchor
                    // the chip on EntityRef.EffectKeyword(...) so clicking it filters the Effects
                    // tab to entries whose Keywords list contains the tag.
                    if (string.IsNullOrEmpty(h.Keyword))
                        return new QuestRequirementDisplay("Has effect: (unknown)", null, false);
                    var chipName = SplitCamelCase(h.Keyword);
                    var reference = EntityRef.EffectKeyword(h.Keyword!);
                    return new QuestRequirementDisplay(
                        Text: $"Has effect: {chipName}",
                        Reference: reference,
                        IsNavigable: navigator.CanOpen(reference),
                        Prefix: "Has effect:",
                        ChipName: chipName);
                }

            case TimeOfDayRequirement t:
                if (!string.IsNullOrEmpty(t.Hours))
                    return new QuestRequirementDisplay($"During {t.Hours!.ToLowerInvariant()}", null, false);
                if (t.MinHour is { } min && t.MaxHour is { } max)
                    return new QuestRequirementDisplay($"Between {min:00}:00 and {max:00}:00 in-game time", null, false);
                return null;

            case MoonPhaseRequirement m:
                return new QuestRequirementDisplay($"Moon phase: {SplitCamelCase(m.MoonPhase)}", null, false);

            case FullMoonRequirement:
                return new QuestRequirementDisplay("During the full moon", null, false);

            case DayOfWeekRequirement d:
                {
                    if (d.DaysAllowed is null || d.DaysAllowed.Count == 0) return null;
                    return new QuestRequirementDisplay($"Days: {string.Join(", ", d.DaysAllowed)}", null, false);
                }

            case AreaEventOnRequirement a:
                return new QuestRequirementDisplay($"Area event active: {SplitCamelCase(a.AreaEvent)}", null, false);

            case AreaEventOffRequirement a:
                return new QuestRequirementDisplay($"Area event inactive: {SplitCamelCase(a.AreaEvent)}", null, false);

            case InHotspotRequirement h:
                return new QuestRequirementDisplay($"Inside hotspot: {SplitCamelCase(h.Name)}", null, false);

            case MonsterTargetLevelRequirement m:
                return new QuestRequirementDisplay($"Targeting a monster of level ≥ {m.MinLevel}", null, false);

            case OtherHasTypeTagRequirement o:
                return new QuestRequirementDisplay($"Target has type tag: {SplitCamelCase(o.TypeTag)}", null, false);

            case InCombatRequirement c:
                return new QuestRequirementDisplay(
                    c.MinLevel is > 0 ? $"In combat (target level ≥ {c.MinLevel})" : "In combat",
                    null, false);

            case InCombatWithEliteRequirement c:
                return new QuestRequirementDisplay(
                    c.MinLevel is > 0 ? $"In combat with elite (level ≥ {c.MinLevel})" : "In combat with an elite",
                    null, false);

            case PetCountRequirement p:
                {
                    var kind = ResolvePetKind(p.PetTypeTag, refData);
                    // Append "pet"/"pets" only when the resolved name isn't already a pet
                    // noun, killing the "… Cow Pet pets" redundancy (recipe-side shape).
                    var kindIsPetNoun =
                        kind.EndsWith("pet", StringComparison.OrdinalIgnoreCase)
                        || kind.EndsWith("pets", StringComparison.OrdinalIgnoreCase);
                    string Noun(int n) => kindIsPetNoun ? kind : $"{kind} pet{(n == 1 ? "" : "s")}";
                    if (p.MinCount is { } mn && p.MaxCount is { } mx && mn != mx)
                        return new QuestRequirementDisplay($"Owns {mn}–{mx} {Noun(mx)}", null, false);
                    var n = p.MinCount ?? p.MaxCount ?? 1;
                    return new QuestRequirementDisplay($"Owns {n} {Noun(n)}", null, false);
                }

            case OrRequirement or:
                {
                    if (or.List is null || or.List.Count == 0) return null;
                    // Render the composite inline by joining child labels with " OR ". Group
                    // bucket assigns this to the "Any of" group, so the OR header is already
                    // visually present at the group label level.
                    var parts = new List<string>(or.List.Count);
                    foreach (var child in or.List)
                    {
                        var projected = ProjectRequirement(child, refData, resolver, navigator);
                        if (projected is not null) parts.Add(projected.Text);
                    }
                    return parts.Count == 0
                        ? null
                        : new QuestRequirementDisplay(string.Join(" • ", parts), null, false);
                }

            case UnknownQuestRequirement u:
                return new QuestRequirementDisplay($"Unrecognised: {u.DiscriminatorValue}", null, false);

            // Internal-flag developer-facing variants. Show the field as a small monospace
            // tag so the player at least sees *what's gating them*, but the cluster bucket
            // signals these aren't actionable.
            case InteractionFlagSetRequirement f:
                return new QuestRequirementDisplay($"Interaction flag set: {f.InteractionFlag}", null, false);
            case InteractionFlagUnsetRequirement f:
                return new QuestRequirementDisplay($"Interaction flag unset: {f.InteractionFlag}", null, false);
            case AccountFlagUnsetRequirement f:
                return new QuestRequirementDisplay($"Account flag unset: {f.AccountFlag}", null, false);
            case ScriptAtomicMatchesRequirement s:
                return new QuestRequirementDisplay($"Atomic '{s.AtomicVar}' = {s.Value}", null, false);
            case AttributeMatchesScriptAtomicRequirement a:
                return new QuestRequirementDisplay($"Attribute '{a.Attribute}' matches atomic '{a.ScriptAtomicInt}'", null, false);
            case RuntimeBehaviorRuleSetRequirement r:
                return new QuestRequirementDisplay($"Behavior rule: {r.Rule}", null, false);
            case GeneralShapeRequirement g:
                return new QuestRequirementDisplay($"General shape: {g.Shape}", null, false);
            case EntityPhysicalStateRequirement e:
                {
                    if (e.AllowedStates is null || e.AllowedStates.Count == 0) return null;
                    return new QuestRequirementDisplay($"Physical state: {string.Join(", ", e.AllowedStates)}", null, false);
                }

            default:
                // Concrete subclass not yet handled (newly-added between releases). Render
                // the raw T value so the gate is visible while highlighting it as drift.
                return new QuestRequirementDisplay($"Unhandled: {req.T}", null, false);
        }
    }

    // ─── Rewards ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Bucket a quest's typed <see cref="Quest.Rewards"/> by intent. Item / recipe rewards
    /// surface as <see cref="EntityChipVm"/> chips on the detail VM (separate collection),
    /// not here — this groups only the value-shaped rewards (XP, currency, abilities).
    /// </summary>
    public static IReadOnlyList<QuestRewardGroup> BuildRewardGroups(
        Quest quest,
        IReferenceDataService refData,
        IEntityNameResolver resolver,
        IReferenceNavigator navigator)
    {
        var xp = new List<QuestRewardDisplay>();
        var currency = new List<QuestRewardDisplay>();
        var abilities = new List<QuestRewardDisplay>();
        var effects = new List<QuestRewardDisplay>();

        if (quest.Rewards is { } rewards)
        {
            foreach (var r in rewards)
            {
                switch (r)
                {
                    case SkillXpReward s:
                        xp.Add(new QuestRewardDisplay($"{ResolveSkillName(refData, s.Skill)}: {s.Xp:N0} XP"));
                        break;
                    case CombatXpReward c:
                        xp.Add(new QuestRewardDisplay($"Combat: {c.Xp:N0} XP{(c.Level is { } lvl ? $" (level {lvl})" : "")}"));
                        break;
                    case GuildXpReward g:
                        xp.Add(new QuestRewardDisplay($"Guild: {g.Xp:N0} XP"));
                        break;
                    case RacingXpReward rc:
                        xp.Add(new QuestRewardDisplay($"Racing — {ResolveSkillName(refData, rc.Skill)}: {rc.Xp:N0} XP"));
                        break;
                    case CurrencyReward cu:
                        currency.Add(new QuestRewardDisplay($"{cu.Amount:N0} {FriendlyCurrencyName(cu.Currency)}"));
                        break;
                    case WorkOrderCurrencyReward wo:
                        currency.Add(new QuestRewardDisplay($"{wo.Amount:N0} {FriendlyCurrencyName(wo.Currency)} (work-order)"));
                        break;
                    case GuildCreditsReward gc:
                        currency.Add(new QuestRewardDisplay($"{gc.Credits:N0} guild credits"));
                        break;
                    case AbilityReward ar:
                        abilities.Add(BuildAbilityChipReward(ar.Ability, navigator));
                        break;
                    case UnknownQuestReward u:
                        effects.Add(new QuestRewardDisplay($"Unrecognised: {u.DiscriminatorValue}"));
                        break;
                    // Recipe rewards are surfaced as clickable EntityChipVm chips in the
                    // detail VM, not as a reward-group entry.
                }
            }
        }

        if (quest.Rewards_Effects is { Count: > 0 } rewardEffects)
        {
            foreach (var e in rewardEffects)
                if (!string.IsNullOrEmpty(e))
                    effects.Add(FormatRewardEffect(e, refData, resolver, navigator));
        }

        var groups = new List<QuestRewardGroup>();
        if (xp.Count > 0) groups.Add(new QuestRewardGroup("Experience", xp));
        if (currency.Count > 0) groups.Add(new QuestRewardGroup("Currency", currency));
        if (abilities.Count > 0) groups.Add(new QuestRewardGroup("Abilities (learned)", abilities));
        if (effects.Count > 0) groups.Add(new QuestRewardGroup("Effects", effects));
        return groups;
    }

    // ─── Objectives ────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<QuestObjectiveRow> BuildObjectives(
        Quest quest,
        IReferenceDataService refData,
        IEntityNameResolver resolver,
        IReferenceNavigator navigator)
    {
        if (quest.Objectives is null || quest.Objectives.Count == 0) return Array.Empty<QuestObjectiveRow>();
        var rows = new List<QuestObjectiveRow>(quest.Objectives.Count);
        for (var i = 0; i < quest.Objectives.Count; i++)
        {
            var obj = quest.Objectives[i];
            var description = string.IsNullOrEmpty(obj.Description)
                ? (obj.Type ?? "(unnamed objective)")
                : obj.Description!;
            var nested = BuildRequirementGroups(obj.Requirements, refData, resolver, navigator);
            rows.Add(new QuestObjectiveRow(
                Index: i + 1,
                Description: description,
                Number: obj.Number > 1 ? obj.Number : null,
                NestedRequirements: nested));
        }
        return rows;
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────────────

    private static string ResolveSkillName(IReferenceDataService refData, string? skillKey)
    {
        if (string.IsNullOrEmpty(skillKey)) return "(unknown skill)";
        return refData.Skills.TryGetValue(skillKey!, out var s) && !string.IsNullOrEmpty(s.DisplayName)
            ? s.DisplayName
            : SplitCamelCase(skillKey);
    }

    private static string ResolveNpcName(IEntityNameResolver resolver, string? npcInternalName) =>
        string.IsNullOrEmpty(npcInternalName)
            ? "(unknown NPC)"
            : resolver.Resolve(EntityRef.Npc(npcInternalName!));

    /// <summary>
    /// Resolve an NPC reference to a player-readable string with the NPC's area in
    /// parentheses when available: <c>"Durstin Tallow (Serbule Hills)"</c>. Quest data
    /// references NPCs in the slug form <c>&lt;AreaName&gt;/&lt;NpcKey&gt;</c>; the
    /// <see cref="EntityRef.Npc"/> factory strips the area prefix for lookup, but the
    /// area is still useful context on the rendered chip / inline favor line. Looks up
    /// the resolved NPC POCO and appends <see cref="Mithril.Reference.Models.Npcs.Npc.AreaFriendlyName"/>
    /// when present; falls back to just the resolved name otherwise.
    /// </summary>
    public static string ResolveNpcDisplayWithArea(
        IReferenceDataService refData,
        IEntityNameResolver resolver,
        string? npcInternalName)
    {
        if (string.IsNullOrEmpty(npcInternalName)) return "(unknown NPC)";
        var canonical = EntityRef.Npc(npcInternalName!).InternalName;
        var name = resolver.Resolve(EntityRef.Npc(npcInternalName!));

        // Primary path: NPC is in npcs.json and has an AreaFriendlyName populated.
        if (refData.NpcsByInternalName.TryGetValue(canonical, out var npc)
            && !string.IsNullOrEmpty(npc.AreaFriendlyName))
        {
            return $"{name} ({npc.AreaFriendlyName})";
        }

        // Fallback: NPC not in npcs.json (event-only NPCs like AreaCasino/LiveNpc_Orran are
        // common — they're scripted entities, not full POCOs). Look up the area portion of the
        // slug via areas.json and strip recognizable NPC-key prefixes from the resolved name so
        // "LiveNpc_Orran" reads as "Orran" rather than as the raw envelope key.
        var slashIdx = npcInternalName!.LastIndexOf('/');
        if (slashIdx > 0)
        {
            var areaKey = npcInternalName!.Substring(0, slashIdx);
            if (refData.Areas.TryGetValue(areaKey, out var area)
                && !string.IsNullOrEmpty(area.FriendlyName))
            {
                return $"{StripNpcKeyPrefixes(name)} ({area.FriendlyName})";
            }
        }
        return name;
    }

    private static string StripNpcKeyPrefixes(string name)
    {
        if (name.StartsWith("LiveNpc_", System.StringComparison.Ordinal)) return name.Substring("LiveNpc_".Length);
        if (name.StartsWith("NPC_", System.StringComparison.Ordinal)) return name.Substring("NPC_".Length);
        return name;
    }

    // ─── Reward-effect strings ─────────────────────────────────────────────────────────
    //
    // Rewards_Effects ships method-call-style strings (e.g. "GiveXP(Carpentry,11000)") —
    // the same dialect as recipe ResultEffects. Naive CamelCase-splitting turns these into
    // unreadable junk ("Give XP( Carpentry,11000)"), so route each effect through a focused
    // parser that handles the top player-facing prefixes and renders unknowns verbatim.
    //
    // Top prefix vocabulary in quests.json (counted via grep):
    //   239  DeltaNpcFavor
    //   162  SetInteractionFlag    (developer-facing — verbatim)
    //    94  GiveXP
    //    72  DeltaScriptAtomicInt  (developer-facing — verbatim)
    //    29  ClearInteractionFlag  (developer-facing — verbatim)
    //    15  RaiseSkillToLevel
    //    17  BestowTitle
    //    14  AdvanceScriptedQuestObjective  (developer-facing — verbatim)
    //    10  LearnAbility
    //     6  EnsureLoreBookKnown
    //     2  BestowRecipe

    public static QuestRewardDisplay FormatRewardEffect(
        string raw,
        IReferenceDataService refData,
        IEntityNameResolver resolver,
        IReferenceNavigator navigator)
    {
        if (string.IsNullOrEmpty(raw)) return new QuestRewardDisplay(raw);
        // No "(" → either a no-arg effect ("GiveWerewolfBuffPoint") or unknown shape. CamelCase
        // split is friendlier than the raw identifier; no chip target.
        var parenIdx = raw.IndexOf('(');
        if (parenIdx < 0) return new QuestRewardDisplay(SplitCamelCase(raw));

        var prefix = raw.Substring(0, parenIdx);
        var argsEnd = raw.LastIndexOf(')');
        if (argsEnd <= parenIdx) return new QuestRewardDisplay(raw);
        var argsRaw = raw.Substring(parenIdx + 1, argsEnd - parenIdx - 1);
        var args = SplitArgs(argsRaw);

        switch (prefix)
        {
            // Value-shaped effects: stay on the prose path.
            case "GiveXP" when args.Count == 2:
                return new QuestRewardDisplay($"{ResolveSkillName(refData, args[0])}: +{FormatNumber(args[1])} XP");
            case "DeltaNpcFavor" when args.Count == 2:
                {
                    var chipName = ResolveNpcDisplayWithArea(refData, resolver, args[0]);
                    var reference = BuildNpcRef(args[0], navigator, out var canOpenNpc);
                    var favorPrefix = $"{FormatSignedAmount(args[1])} favor with";
                    return new QuestRewardDisplay(
                        Text: $"{favorPrefix} {chipName}",
                        Reference: reference,
                        IsNavigable: canOpenNpc,
                        Prefix: favorPrefix,
                        ChipName: chipName);
                }
            case "RaiseSkillToLevel" when args.Count == 2:
                return new QuestRewardDisplay($"Raise {ResolveSkillName(refData, args[0])} to level {args[1]}");

            // Entity-shaped effects: chip stubs. Each lights up the moment its target tab ships;
            // until then the chip degrades to plain-text via _navigator.CanOpen.
            case "BestowTitle" when args.Count >= 1:
                return BuildEntityChipReward("Earns title:", EntityRef.PlayerTitle(args[0]), SplitCamelCase(args[0]), navigator);
            case "LearnAbility" when args.Count >= 1:
                return BuildEntityChipReward("Teaches ability:", EntityRef.Ability(args[0]), SplitCamelCase(args[0]), navigator);
            case "BestowRecipe" when args.Count >= 1:
                {
                    var reference = EntityRef.Recipe(args[0]);
                    return BuildEntityChipReward("Teaches recipe:", reference, resolver.Resolve(reference), navigator);
                }
            case "EnsureLoreBookKnown" when args.Count >= 1:
                return BuildEntityChipReward("Lorebook:", EntityRef.Lorebook(args[0]), SplitCamelCase(args[0]), navigator);

            // Developer-facing effects render verbatim — they're not gameplay rewards, but the
            // player / curious dev can see *that* the quest tweaks internal state.
            default:
                return new QuestRewardDisplay(raw);
        }
    }

    private static QuestRewardDisplay BuildEntityChipReward(
        string prefix,
        EntityRef reference,
        string chipName,
        IReferenceNavigator navigator) =>
        new(
            Text: $"{prefix} {chipName}",
            Reference: reference,
            IsNavigable: navigator.CanOpen(reference),
            Prefix: prefix,
            ChipName: chipName);

    private static QuestRewardDisplay BuildAbilityChipReward(string? ability, IReferenceNavigator navigator)
    {
        if (string.IsNullOrEmpty(ability)) return new QuestRewardDisplay("(unknown ability)");
        var chipName = SplitCamelCase(ability);
        var reference = EntityRef.Ability(ability!);
        return new QuestRewardDisplay(
            Text: chipName,
            Reference: reference,
            IsNavigable: navigator.CanOpen(reference),
            Prefix: null,         // no label — the group header says "Abilities (learned)"
            ChipName: chipName);
    }

    /// <summary>
    /// Split a comma-separated argument list, trimming surrounding whitespace from each piece.
    /// Doesn't try to handle nested commas (PG's effect strings don't nest), keeps it cheap.
    /// </summary>
    private static IReadOnlyList<string> SplitArgs(string args)
    {
        if (string.IsNullOrEmpty(args)) return System.Array.Empty<string>();
        var parts = args.Split(',');
        var result = new string[parts.Length];
        for (var i = 0; i < parts.Length; i++) result[i] = parts[i].Trim();
        return result;
    }

    private static string FormatNumber(string raw) =>
        long.TryParse(raw, System.Globalization.NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n.ToString("N0", CultureInfo.InvariantCulture)
            : raw;

    private static string FormatSignedAmount(string raw)
    {
        if (long.TryParse(raw, System.Globalization.NumberStyles.Integer | System.Globalization.NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var n))
        {
            return n >= 0 ? $"+{n:N0}" : n.ToString("N0", CultureInfo.InvariantCulture);
        }
        return raw;
    }

    private static EntityRef? BuildNpcRef(string? npcInternalName, IReferenceNavigator navigator, out bool isNavigable)
    {
        if (string.IsNullOrEmpty(npcInternalName))
        {
            isNavigable = false;
            return null;
        }
        var reference = EntityRef.Npc(npcInternalName!);
        isNavigable = navigator.CanOpen(reference);
        return reference;
    }

    private static QuestRequirementDisplay BuildQuestChip(
        string? questInternalName,
        string prefix,
        IEntityNameResolver resolver,
        IReferenceNavigator navigator)
    {
        if (string.IsNullOrEmpty(questInternalName))
            return new QuestRequirementDisplay($"{prefix} (unknown)", null, false);
        var name = resolver.Resolve(EntityRef.Quest(questInternalName!));
        var reference = EntityRef.Quest(questInternalName!);
        return new QuestRequirementDisplay(
            Text: $"{prefix} {name}",
            Reference: reference,
            IsNavigable: navigator.CanOpen(reference),
            Prefix: prefix,
            ChipName: name);
    }

    private static QuestRequirementDisplay BuildItemChip(
        string? itemInternalName,
        string prefix,
        IEntityNameResolver resolver,
        IReferenceDataService refData,
        IReferenceNavigator navigator)
    {
        if (string.IsNullOrEmpty(itemInternalName))
            return new QuestRequirementDisplay($"{prefix} (unknown item)", null, false);
        var name = resolver.Resolve(EntityRef.Item(itemInternalName!));
        var reference = EntityRef.Item(itemInternalName!);
        return new QuestRequirementDisplay(
            Text: $"{prefix} {name}",
            Reference: reference,
            IsNavigable: navigator.CanOpen(reference),
            Prefix: prefix,
            ChipName: name);
    }

    /// <summary>
    /// Resolve a <c>PetTypeTag</c> to its real in-game display name. PG models pet/summon
    /// types as NPC/monster entities, so the name lives in <c>strings_all</c> under
    /// <c>npc_&lt;tag&gt;_Name</c> ("SummonedBakingBread" → "Rising Dough"). These tags are
    /// <em>not</em> in <c>npcs.json</c>, so <see cref="IEntityNameResolver"/>'s NPC path
    /// can't reach them — consult <see cref="IReferenceDataService.Strings"/> directly (the
    /// id→display-name convention; mirrors <c>RecipeRequirementProjector.DescribePetCount</c>
    /// / PR #400). <see cref="SplitCamelCase"/> is the fallback when the key is absent.
    /// <para>
    /// <b>Per-tag audit — issue #405.</b> Only <c>PetTypeTag</c> resolves through
    /// <c>strings_all</c>. Every other quest requirement tag that still falls back to
    /// <see cref="SplitCamelCase"/> was checked against the bundled string table and has
    /// <em>no</em> reliable, non-coincidental key — speculatively resolving them risks a
    /// coincidental wrong label (the recipe-side rationale this mirrors):
    /// <list type="bullet">
    ///   <item><description><c>AppearanceRequirement.Appearance</c> ("Rabbit"): no
    ///   <c>npc_Rabbit_Name</c> / <c>appearance_*</c> key exists; resolving via the rabbit
    ///   monster would mislabel a player morph. Stays CamelCase-split.</description></item>
    ///   <item><description><c>RaceRequirement</c> ("Fae"), <c>OtherHasTypeTagRequirement</c>
    ///   ("Sentient"), <c>EquipmentSlotEmptyRequirement.Slot</c> ("Head"/"Chest"/…),
    ///   <c>MoonPhaseRequirement</c>, <c>AreaEvent*Requirement</c>,
    ///   <c>EquippedItemKeywordRequirement</c>, <c>HangOutCompletedRequirement</c>: no
    ///   string-table key pattern; the CamelCase split already reads cleanly.</description></item>
    /// </list>
    /// (<c>HasEffectKeywordRequirement</c> is separately chip-anchored on
    /// <see cref="EntityRef.EffectKeyword"/>; abilities get the future Abilities-tab
    /// resolver.) Do not broaden this without repeating the audit.
    /// </para>
    /// </summary>
    private static string ResolvePetKind(string? petTypeTag, IReferenceDataService refData)
    {
        if (string.IsNullOrEmpty(petTypeTag)) return "pet";
        return refData.Strings.TryGetValue($"npc_{petTypeTag}_Name", out var resolved)
               && !string.IsNullOrEmpty(resolved)
            ? resolved!
            : SplitCamelCase(petTypeTag);
    }

    /// <summary>
    /// Insert spaces before uppercase letters inside a CamelCase identifier so display reads
    /// as a phrase: <c>"MoonPhase"</c> → <c>"Moon Phase"</c>, <c>"FullMoon"</c> → <c>"Full Moon"</c>.
    /// Underscores are treated as explicit word boundaries (deduped against any space the
    /// camel-case rule would also insert), so <c>"LiveEvent_Crafting"</c> reads as
    /// <c>"Live Event Crafting"</c> rather than <c>"Live Event_ Crafting"</c>. This matters
    /// because chip-stub fallbacks (HasEffectKeyword / BestowTitle / LearnAbility /
    /// EnsureLoreBookKnown) hit this path until each target tab ships its proper resolver.
    /// Strings shorter than 2 chars pass through unchanged; null/empty returns "(unknown)".
    /// </summary>
    private static string SplitCamelCase(string? input)
    {
        if (string.IsNullOrEmpty(input)) return "(unknown)";
        if (input!.Length < 2) return input;
        var sb = new System.Text.StringBuilder(input.Length + 4);
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '_')
            {
                if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                continue;
            }
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(input[i - 1])
                && sb.Length > 0 && sb[sb.Length - 1] != ' ')
            {
                sb.Append(' ');
            }
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static string FriendlyCurrencyName(string? currency) =>
        string.IsNullOrEmpty(currency) ? "currency" : SplitCamelCase(currency!).ToLowerInvariant() switch
        {
            "councils" => "Councils",
            "cera" => "Cera",
            _ => SplitCamelCase(currency!),
        };
}
