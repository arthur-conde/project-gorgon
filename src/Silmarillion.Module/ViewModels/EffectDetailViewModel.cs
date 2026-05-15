using System.Globalization;
using System.Windows.Input;
using Mithril.Reference.Models.Abilities;
using Mithril.Reference.Models.Misc;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using PocoEffect = Mithril.Reference.Models.Effects.Effect;

namespace Silmarillion.ViewModels;

/// <summary>
/// Read-only projection of an <see cref="PocoEffect"/> for the Silmarillion Effects tab
/// detail pane. Hostable in both the master-detail right pane and the popup
/// <see cref="Silmarillion.Views.EffectDetailWindow"/>.
/// <para>
/// Nine sections: header, formatted description, metadata strip (duration + stacking type
/// + display mode), keyword chips, conditional-rule sub-tables fed by
/// <see cref="IReferenceDataService.AbilityDynamicDots"/> and
/// <see cref="IReferenceDataService.AbilityDynamicSpecialValues"/>, stacks-with cluster
/// (other effects sharing <see cref="PocoEffect.StackingType"/>), required-by-abilities
/// cluster with cap + overflow pill, procs-from-abilities-with-keyword rows, and a
/// SpewText footer.
/// </para>
/// </summary>
public sealed class EffectDetailViewModel
{
    public EffectDetailViewModel(
        PocoEffect effect,
        string envelopeKey,
        IReferenceDataService refData,
        IReferenceNavigator navigator,
        IEntityNameResolver nameResolver,
        Silmarillion.SilmarillionSettings settings,
        ICommand? openEntityCommand = null)
    {
        Effect = effect;
        EnvelopeKey = envelopeKey;
        DisplayName = string.IsNullOrEmpty(effect.Name) ? envelopeKey : effect.Name!;

        DurationLabel = FormatDuration(effect.Duration);
        DisplayMode = NormaliseDisplayMode(effect.DisplayMode);

        KeywordChips = BuildKeywordChips(effect.Keywords, navigator);
        ConditionalDotRows = BuildConditionalDotRows(effect, refData);
        ConditionalSpecialValueRows = BuildConditionalSpecialValueRows(effect, refData);
        StackingTypeChip = BuildStackingTypeChip(effect, envelopeKey, refData, navigator);
        var (chips, shortcut) = BuildRequiredByAbilityChips(effect, refData, nameResolver, navigator, settings.RequiredByAbilitiesChipCap);
        RequiredByAbilityChips = chips;
        RequiredByAbilitiesTabShortcut = shortcut;
        ProcsFromAbilityKeywordRows = BuildProcsFromAbilityKeywordRows(effect.AbilityKeywords);

        SpewText = string.IsNullOrEmpty(effect.SpewText) ? null : effect.SpewText;

        OpenEntityCommand = openEntityCommand;
    }

    public PocoEffect Effect { get; }
    public string EnvelopeKey { get; }
    public string DisplayName { get; }
    public int IconId => Effect.IconId;
    public string? Description => Effect.Desc;

    /// <summary>Formatted duration label or <see langword="null"/> when the duration is missing/zero.</summary>
    public string? DurationLabel { get; }

    /// <summary>
    /// <see cref="PocoEffect.DisplayMode"/> when non-default. The universal default
    /// <c>"Effect"</c> is filtered out so the chip only surfaces for entries with
    /// information content (e.g. <c>"Cocoon"</c>, <c>"Curse"</c>).
    /// </summary>
    public string? DisplayMode { get; }

    public IReadOnlyList<EntityChipVm> KeywordChips { get; }
    public IReadOnlyList<EffectConditionalDotRow> ConditionalDotRows { get; }
    public IReadOnlyList<EffectConditionalSpecialValueRow> ConditionalSpecialValueRows { get; }

    /// <summary>True when at least one conditional sub-table row applies — drives the section header visibility.</summary>
    public bool HasConditionalRuleRows =>
        ConditionalDotRows.Count > 0 || ConditionalSpecialValueRows.Count > 0;

    /// <summary>
    /// Chip for the metadata strip's <c>"Stacking type:"</c> row — label is the StackingType
    /// value plus a peer-count suffix (e.g. <c>"Food (325)"</c>), reference is
    /// <see cref="EntityRef.EffectByStackingType(string)"/>. Clicking filters the Effects
    /// tab to <c>StackingType = "&lt;value&gt;"</c>. Null when the effect has no StackingType
    /// or is the sole entry in the stacking group (no peers means no useful filter target,
    /// per the cookbook's default-value-noise-filtering rule).
    /// <para>
    /// Folds what was originally a separate "Stacks with" section into the metadata strip:
    /// the chip is the StackingType's navigation affordance directly. Per #259's
    /// keyword-collapse precedent — don't fan out to per-effect chips when cardinality
    /// could be large (the "Food" stacking group alone has ~326 entries).
    /// </para>
    /// </summary>
    public EntityChipVm? StackingTypeChip { get; }
    public IReadOnlyList<EntityChipVm> RequiredByAbilityChips { get; }

    /// <summary>
    /// Always-visible navigable summary chip rendered after the capped
    /// <see cref="RequiredByAbilityChips"/>. Non-null whenever the effect is required by
    /// any ability (independent of <c>SilmarillionSettings.RequiredByAbilitiesChipCap</c>).
    /// Anchored on <see cref="EntityRef.AbilityByEffectKeyword"/> so clicking opens the
    /// Abilities tab filtered by <c>EffectKeywordReqs CONTAINS "&lt;tag&gt;"</c>; rendered
    /// as <c>ActionChip</c>.
    /// </summary>
    public EntityChipVm? RequiredByAbilitiesTabShortcut { get; }

    public IReadOnlyList<EffectProcsFromAbilityKeywordRow> ProcsFromAbilityKeywordRows { get; }
    public string? SpewText { get; }

    public ICommand? OpenEntityCommand { get; }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Render the Duration field. JSON ships either an int second count, the literal string
    /// <c>"Permanent"</c>, or a sentinel int (<c>-1</c>, <c>-2</c>). The sentinel meanings are
    /// best-guess labels — Elder Game doesn't publish a key and reverse-engineering from
    /// bundled data suggests <c>-1</c> = until cleansed and <c>-2</c> = until removed (e.g.
    /// <c>effect_10003</c> Sticky! is <c>-2</c>). If a future PG patch contradicts this,
    /// the projection needs to change — there's no automatic drift detector.
    /// </summary>
    private static string? FormatDuration(string? duration)
    {
        if (string.IsNullOrEmpty(duration)) return null;
        if (string.Equals(duration, "Permanent", StringComparison.Ordinal)) return "Permanent";
        if (!int.TryParse(duration, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return duration;

        if (seconds == 0) return null;
        if (seconds == -1) return "Until cleansed";
        if (seconds == -2) return "Until removed";
        if (seconds < 0) return duration;
        if (seconds < 60) return $"{seconds} second{(seconds == 1 ? "" : "s")}";
        if (seconds < 3600)
        {
            var mins = seconds / 60;
            var remSec = seconds % 60;
            return remSec == 0
                ? $"{mins} minute{(mins == 1 ? "" : "s")}"
                : $"{mins} min {remSec} sec";
        }
        var hours = seconds / 3600;
        var remMin = (seconds % 3600) / 60;
        return remMin == 0
            ? $"{hours} hour{(hours == 1 ? "" : "s")}"
            : $"{hours} hr {remMin} min";
    }

    /// <summary>
    /// Filter out <see cref="PocoEffect.DisplayMode"/>'s universal default so the chip
    /// only renders when it carries information per the cookbook's <em>Default-value
    /// noise filtering</em> rule. <c>"Effect"</c> dominates the bundled file (the
    /// implicit default when JSON omits the field); rare values like <c>"Cocoon"</c>
    /// or <c>"Curse"</c> are the player-facing variants.
    /// </summary>
    private static string? NormaliseDisplayMode(string? raw) =>
        string.IsNullOrEmpty(raw) || string.Equals(raw, "Effect", StringComparison.Ordinal)
            ? null
            : raw;

    private static IReadOnlyList<EntityChipVm> BuildKeywordChips(
        IReadOnlyList<string>? keywords,
        IReferenceNavigator navigator)
    {
        if (keywords is null || keywords.Count == 0) return [];
        var list = new List<EntityChipVm>(keywords.Count);
        foreach (var tag in keywords)
        {
            if (string.IsNullOrEmpty(tag)) continue;
            var reference = EntityRef.EffectKeyword(tag);
            list.Add(new EntityChipVm(
                DisplayName: tag,
                IconId: 0,
                Reference: reference,
                IsNavigable: navigator.CanOpen(reference)));
        }
        return list;
    }

    private static IReadOnlyList<EffectConditionalDotRow> BuildConditionalDotRows(
        PocoEffect effect, IReferenceDataService refData)
    {
        var rules = refData.AbilityDynamicDots;
        if (rules.Count == 0) return [];

        var list = new List<EffectConditionalDotRow>();
        foreach (var rule in rules)
        {
            if (!AbilityRulePredicate.Matches(rule.ReqEffectKeywords, effect.Keywords)) continue;
            var display = FormatDotRule(rule);
            list.Add(new EffectConditionalDotRow(display, rule.ReqAbilityKeywords ?? []));
        }
        return list;
    }

    private static string FormatDotRule(AbilityDynamicDot rule)
    {
        // "Deals N damage per tick × M ticks of <DamageType>" — keep the format close to the
        // bundled-data shape so the rendered prose matches in-game tooltip expectations.
        var damageType = string.IsNullOrEmpty(rule.DamageType) ? "(unspecified)" : rule.DamageType;
        var body = $"{rule.DamagePerTick} damage × {rule.NumTicks} ticks of {damageType}";

        var gates = new List<string>();
        if (rule.ReqAbilityKeywords is { Count: > 0 } abKw)
            gates.Add($"ability keywords [{string.Join(", ", abKw)}]");
        if (!string.IsNullOrEmpty(rule.ReqActiveSkill))
            gates.Add($"active skill {rule.ReqActiveSkill}");

        return gates.Count > 0 ? $"{body}, gated by {string.Join(" + ", gates)}" : body;
    }

    private static IReadOnlyList<EffectConditionalSpecialValueRow> BuildConditionalSpecialValueRows(
        PocoEffect effect, IReferenceDataService refData)
    {
        var rules = refData.AbilityDynamicSpecialValues;
        if (rules.Count == 0) return [];

        var list = new List<EffectConditionalSpecialValueRow>();
        foreach (var rule in rules)
        {
            if (!AbilityRulePredicate.Matches(rule.ReqEffectKeywords, effect.Keywords)) continue;
            if (rule.SkipIfZero == true && rule.Value == 0) continue;
            list.Add(new EffectConditionalSpecialValueRow(
                FormatSpecialValueRule(rule),
                rule.ReqAbilityKeywords ?? []));
        }
        return list;
    }

    private static string FormatSpecialValueRule(AbilityDynamicSpecialValue rule)
    {
        var label = string.IsNullOrEmpty(rule.Label) ? "(value)" : rule.Label;
        var value = rule.Value.ToString("0.##", CultureInfo.InvariantCulture);
        var suffix = string.IsNullOrEmpty(rule.Suffix) ? "" : $" {rule.Suffix}";
        var body = $"{label} {value}{suffix}";

        if (rule.ReqAbilityKeywords is { Count: > 0 } abKw)
            return $"{body}, gated by ability keywords [{string.Join(", ", abKw)}]";
        return body;
    }

    /// <summary>
    /// Build the metadata-strip StackingType chip. Returns <see langword="null"/> when the
    /// effect has no StackingType or is the only entry in its stacking group — the entire
    /// "Stacking type:" row hides in those cases (the chip *is* the row's payload, so
    /// no chip means no row).
    /// </summary>
    private static EntityChipVm? BuildStackingTypeChip(
        PocoEffect effect,
        string envelopeKey,
        IReferenceDataService refData,
        IReferenceNavigator navigator)
    {
        if (string.IsNullOrEmpty(effect.StackingType)) return null;
        if (!refData.EffectsByStackingType.TryGetValue(effect.StackingType!, out var peers)) return null;

        var peerCount = 0;
        foreach (var peer in peers)
        {
            if (peer.InternalName is null) continue;
            if (peer.InternalName == envelopeKey) continue;
            peerCount++;
        }
        if (peerCount == 0) return null;

        var reference = EntityRef.EffectByStackingType(effect.StackingType!);
        return new EntityChipVm(
            DisplayName: $"{effect.StackingType} ({peerCount})",
            IconId: 0,
            Reference: reference,
            IsNavigable: navigator.CanOpen(reference));
    }

    /// <summary>
    /// Build the cap-limited "Required by abilities" chip list plus an always-visible
    /// navigable summary chip (the shortcut into the filtered Abilities tab). Reads the
    /// union of <c>AbilitiesByEffectKeyword[tag]</c> across every tag in
    /// <see cref="PocoEffect.Keywords"/> (the index excludes
    /// <see cref="Ability.InternalAbility"/> rows already). Dedupes by InternalName and
    /// sorts by Skill then Level so the chip cluster reads consistently. The shortcut is
    /// emitted whenever any ability requires the effect — even within the cap it offers a
    /// jump to the sortable/filterable Abilities-tab view.
    /// </summary>
    private static (IReadOnlyList<EntityChipVm> Chips, EntityChipVm? Shortcut) BuildRequiredByAbilityChips(
        PocoEffect effect,
        IReferenceDataService refData,
        IEntityNameResolver nameResolver,
        IReferenceNavigator navigator,
        int cap)
    {
        if (effect.Keywords is null || effect.Keywords.Count == 0) return ([], null);

        var ordered = new List<Ability>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Capture the first matching keyword for the shortcut payload — picking
        // *some* keyword is unavoidable, and "first" is stable and matches the order
        // the player sees in the Keywords chip strip immediately above.
        string? shortcutKeyword = null;

        foreach (var tag in effect.Keywords)
        {
            if (string.IsNullOrEmpty(tag)) continue;
            if (!refData.AbilitiesByEffectKeyword.TryGetValue(tag, out var matches)) continue;
            shortcutKeyword ??= tag;
            foreach (var match in matches)
            {
                // Slice 1 (#318): the index now carries provenance
                // (EffectAbilityMatch.Reason). This reader is adapted minimally —
                // membership and the "View all N" count are unchanged. Distinct-by-
                // InternalName across tags is preserved here; the index is already
                // dedup'd per tag, so the count equals distinct members. The
                // provenance is surfaced by the popup in slice 2.
                var ability = match.Ability;
                if (ability.InternalName is null) continue;
                if (!seen.Add(ability.InternalName)) continue;
                ordered.Add(ability);
            }
        }

        if (ordered.Count == 0) return ([], null);

        ordered.Sort(static (a, b) =>
        {
            var skillCmp = StringComparer.OrdinalIgnoreCase.Compare(a.Skill, b.Skill);
            if (skillCmp != 0) return skillCmp;
            return a.Level.CompareTo(b.Level);
        });

        var visibleCount = Math.Min(cap, ordered.Count);
        var chips = new List<EntityChipVm>(visibleCount);
        for (var i = 0; i < visibleCount; i++)
        {
            var ability = ordered[i];
            var reference = EntityRef.Ability(ability.InternalName!);
            chips.Add(new EntityChipVm(
                DisplayName: nameResolver.Resolve(reference),
                IconId: ability.IconID,
                Reference: reference,
                IsNavigable: navigator.CanOpen(reference)));
        }

        // shortcutKeyword is guaranteed non-null here: ordered.Count > 0 implies at least
        // one keyword matched and set it before its abilities were appended.
        var shortcutReference = EntityRef.AbilityByEffectKeyword(shortcutKeyword!);
        var shortcut = new EntityChipVm(
            DisplayName: $"View all {ordered.Count} in Abilities tab →",
            IconId: 0,
            Reference: shortcutReference,
            IsNavigable: navigator.CanOpen(shortcutReference));

        return (chips, shortcut);
    }

    private static IReadOnlyList<EffectProcsFromAbilityKeywordRow> BuildProcsFromAbilityKeywordRows(
        IReadOnlyList<string>? abilityKeywords)
    {
        if (abilityKeywords is null || abilityKeywords.Count == 0) return [];
        var list = new List<EffectProcsFromAbilityKeywordRow>(abilityKeywords.Count);
        foreach (var tag in abilityKeywords)
        {
            if (string.IsNullOrEmpty(tag)) continue;
            list.Add(new EffectProcsFromAbilityKeywordRow(tag));
        }
        return list;
    }
}

/// <summary>
/// One row in <see cref="EffectDetailViewModel.ConditionalDotRows"/>. Plain projected text
/// (predicate rules don't deep-link to entity rows). <paramref name="GatingAbilityKeywords"/>
/// is surfaced for tooltip / future filtering use.
/// </summary>
public sealed record EffectConditionalDotRow(string Display, IReadOnlyList<string> GatingAbilityKeywords);

/// <summary>One row in <see cref="EffectDetailViewModel.ConditionalSpecialValueRows"/>.</summary>
public sealed record EffectConditionalSpecialValueRow(string Display, IReadOnlyList<string> GatingAbilityKeywords);

/// <summary>
/// One row in <see cref="EffectDetailViewModel.ProcsFromAbilityKeywordRows"/>. The
/// <paramref name="Tag"/> is rendered as plain text in v1 — no synthetic kind exists
/// yet for the abilities-keyword pivot (<see cref="EntityKind.AbilityByEffectKeyword"/>
/// is for a different field), so navigation deferred to a follow-up.
/// </summary>
public sealed record EffectProcsFromAbilityKeywordRow(string Tag);
