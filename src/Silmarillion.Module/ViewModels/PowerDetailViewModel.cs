using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;

namespace Silmarillion.ViewModels;

/// <summary>
/// Read-only projection of a Treasure-System <see cref="PowerEntry"/> for the
/// Silmarillion Treasure tab's Power detail pane (#435), hostable in both the
/// master-detail right pane and the popup <see cref="Silmarillion.Views.TreasureDetailWindow"/>.
/// <para>
/// Every region is classed to exactly one ratified tier per
/// <c>docs/agent-plans/2026-05-18-silmarillion-412-treasure-detail-ratified-spec.md</c>:
/// the title is the <c>InternalName</c> <b>verbatim</b> (Q1 — no humanize / no
/// affix-derivation); the affix is illustrative Fact-body only (present parts only);
/// Skill is a Confirmed Link (Degraded only if the Skills surface is unshipped); Slots
/// are tag-form Set-references; the Tiers ladder is the CF#3 FactTable
/// (<see cref="TreasureTierLadderVm"/>); Pools are Confirmed <c>layers</c> Links; Recipes
/// are a Control disclosure → provenance popup of Confirmed Links; the footer is the
/// G-a KEY (copyable, = title) + ROW (<c>power_NNNN</c>, inert).
/// </para>
/// </summary>
public sealed class PowerDetailViewModel
{
    /// <summary>
    /// Host-supplied opener for the recipes provenance popup. Defaults to a real window;
    /// tests swap a capturing delegate so the VM is fully assertable without a window.
    /// Mirrors <see cref="EffectDetailViewModel.ProvenancePopupOpener"/> — opening this
    /// way never calls the navigator, so it pushes no back/forward history.
    /// </summary>
    public static Action<ProvenancePopupViewModel, ICommand?> ProvenancePopupOpener { get; set; }
        = ShowProvenancePopupWindow;

    public PowerDetailViewModel(
        PowerEntry power,
        IReferenceDataService refData,
        IReferenceNavigator navigator,
        IEntityNameResolver nameResolver,
        ICommand? openEntityCommand = null)
    {
        Power = power;
        // Q1 ruling: InternalName verbatim. The resolver returns it unchanged for
        // EntityKind.Power; calling through it keeps the seam honest rather than
        // hard-coding the bypass here.
        DisplayName = nameResolver.Resolve(EntityRef.Power(power.InternalName));
        SkillDisplay = ResolveSkillDisplay(refData, power.Skill);

        (HasAffix, AffixIllustration) = BuildAffixIllustration(power.Prefix, power.Suffix);

        SkillLink = BuildSkillLink(power.Skill, nameResolver, navigator);
        SlotSetRefs = (power.Slots ?? (IReadOnlyList<string>)[])
            .Where(s => !string.IsNullOrEmpty(s))
            // Constraint members, not a live filter — tag-form, not actionable
            // (availability corollary: still the blue Set-ref chassis, inert click).
            .Select(s => new SetRefVm(s, MatchCount: null, IsActionable: false))
            .ToList();

        TierLadder = TreasureTierLadderVm.Build(power, refData.Attributes, SkillDisplay ?? "");

        PoolLinks = BuildPoolLinks(power.InternalName, refData, nameResolver, navigator);

        var (recipeChips, recipeTotal, recipePopup) =
            BuildRecipesPopup(power.InternalName, refData, nameResolver, navigator);
        RecipeChipCount = recipeChips;
        RecipesTotal = recipeTotal;
        RecipesPopup = recipePopup;

        OpenEntityCommand = openEntityCommand;
        ShowRecipesPopupCommand = new RelayCommand(
            () => ProvenancePopupOpener(RecipesPopup!, OpenEntityCommand),
            () => RecipesPopup is not null);

        // G-a footer. KEY = InternalName: a cross-entity reference key (recipes/profiles
        // join by it) ⇒ copyable. ROW = power_NNNN: storage-only ⇒ inert. The KEY text
        // duplicates the Fact-title text by design (Q1 consequence) — the strip stays
        // because it carries the copy affordance the title does not.
        var ids = new List<FactFooterId>(2)
        {
            new("KEY", power.InternalName, copyable: true),
        };
        if (!string.IsNullOrEmpty(power.EnvelopeKey))
            ids.Add(new FactFooterId("ROW", power.EnvelopeKey, copyable: false));
        Footer = FactFooterVm.Of(ids.ToArray());
    }

    private static void ShowProvenancePopupWindow(ProvenancePopupViewModel vm, ICommand? chipClick) =>
        new ProvenancePopupWindow { DataContext = vm, ChipClickCommand = chipClick }.Show();

    public PowerEntry Power { get; }

    /// <summary>The gold Cambria Fact-title — <c>InternalName</c> verbatim (Q1).</summary>
    public string DisplayName { get; }

    /// <summary>Resolved skill display name (e.g. "Sword"); null when the power has no skill.</summary>
    public string? SkillDisplay { get; }

    /// <summary>True when the power carries at least one affix part to illustrate.</summary>
    public bool HasAffix { get; }

    /// <summary>
    /// The illustrative affix Fact-body sentence — present parts only, framed as
    /// approximate ("appears on items roughly as …, illustrative, not the canonical
    /// name"). Never a reconstructed canonical item name (Q1 / #434). Empty when
    /// <see cref="HasAffix"/> is false.
    /// </summary>
    public string AffixIllustration { get; }

    /// <summary>Skill cross-link — <c>sparkles</c> glyph, Confirmed (Degraded if the
    /// Skills surface is unshipped — <c>IsNavigable</c> handles that at rest-identical).</summary>
    public LinkVm? SkillLink { get; }

    /// <summary>Equip-slot constraint set — tag-form Set-references (not navigation).</summary>
    public IReadOnlyList<SetRefVm> SlotSetRefs { get; }

    /// <summary>The Tiers ladder (CF#3 FactTable). Null ⇒ N=0, the section self-hides.</summary>
    public TreasureTierLadderVm? TierLadder { get; }

    /// <summary>"Appears in pools" — Confirmed <c>layers</c> Links (authoritative
    /// <c>tsysprofiles</c> join; G-d does not apply).</summary>
    public IReadOnlyList<LinkVm> PoolLinks { get; }

    /// <summary>Distinct recipe count behind the disclosure (the "(~N)" affordance).</summary>
    public int RecipeChipCount { get; }

    /// <summary>Same as <see cref="RecipeChipCount"/> — kept for the popup headline parity.</summary>
    public int RecipesTotal { get; }

    /// <summary>The recipes provenance popup (Confirmed Links); null when none roll this power.</summary>
    public ProvenancePopupViewModel? RecipesPopup { get; }

    /// <summary>True when there is at least one recipe — drives the disclosure visibility.</summary>
    public bool HasRecipes => RecipesPopup is not null;

    public ICommand ShowRecipesPopupCommand { get; }
    public ICommand? OpenEntityCommand { get; }

    /// <summary>G-a footer: copyable KEY (InternalName) + inert ROW (power_NNNN).</summary>
    public FactFooterVm Footer { get; }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string? ResolveSkillDisplay(IReferenceDataService refData, string? skillKey)
    {
        if (string.IsNullOrEmpty(skillKey)) return null;
        if (refData.Skills.TryGetValue(skillKey!, out var s) && !string.IsNullOrEmpty(s.DisplayName))
            return s.DisplayName;
        return skillKey;
    }

    /// <summary>
    /// Compose the illustrative affix sentence from whichever parts exist. The «item»
    /// slot is a literal ellipsis placeholder — never a reconstructed item name. Phrased
    /// as approximate per the #434 ruling (affix logic is non-replicable engine-side).
    /// </summary>
    private static (bool HasAffix, string Text) BuildAffixIllustration(string? prefix, string? suffix)
    {
        var hasPrefix = !string.IsNullOrWhiteSpace(prefix);
        var hasSuffix = !string.IsNullOrWhiteSpace(suffix);
        if (!hasPrefix && !hasSuffix) return (false, "");

        string shape = (hasPrefix, hasSuffix) switch
        {
            (true, true) => $"“{prefix!.Trim()} … {suffix!.Trim()}”",
            (true, false) => $"“{prefix!.Trim()} …”",
            _ => $"“… {suffix!.Trim()}”",
        };
        return (true, $"Appears on items roughly as {shape} — illustrative, not the canonical name.");
    }

    private static LinkVm? BuildSkillLink(
        string? skillKey,
        IEntityNameResolver resolver,
        IReferenceNavigator navigator)
    {
        if (string.IsNullOrEmpty(skillKey)) return null;
        var reference = EntityRef.Skill(skillKey!);
        var chip = new EntityChipVm(
            DisplayName: resolver.Resolve(reference),
            IconId: 0,
            Reference: reference,
            IsNavigable: navigator.CanOpen(reference));
        return LinkVm.From(chip);
    }

    private static IReadOnlyList<LinkVm> BuildPoolLinks(
        string powerInternalName,
        IReferenceDataService refData,
        IEntityNameResolver resolver,
        IReferenceNavigator navigator)
    {
        if (!refData.ProfilesByPower.TryGetValue(powerInternalName, out var profiles) || profiles.Count == 0)
            return [];
        return profiles
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(profileName =>
            {
                var reference = EntityRef.Profile(profileName);
                return new LinkVm(
                    DisplayName: resolver.Resolve(reference),
                    Glyph: LinkGlyph.Pool,
                    Reference: reference,
                    IsNavigable: navigator.CanOpen(reference));
            })
            .ToList();
    }

    /// <summary>
    /// Build the Power→Recipe provenance popup. Chain (every hop an authoritative
    /// normalized lookup, so Confirmed — Q4): power → profiles containing it
    /// (<see cref="IReferenceDataService.ProfilesByPower"/>) → items whose
    /// <c>TSysProfile</c> is one of those (<see cref="IReferenceDataService.ItemsByTSysProfile"/>)
    /// → recipes producing those items (<see cref="IReferenceDataService.RecipesByProducedItem"/>).
    /// Single-reason ⇒ one section ⇒ the popup collapses to a flat list (#318 Discipline).
    /// Materialized once here from the indices directly — no query re-derivation.
    /// </summary>
    private static (int Count, int Total, ProvenancePopupViewModel? Popup) BuildRecipesPopup(
        string powerInternalName,
        IReferenceDataService refData,
        IEntityNameResolver resolver,
        IReferenceNavigator navigator)
    {
        if (!refData.ProfilesByPower.TryGetValue(powerInternalName, out var profiles) || profiles.Count == 0)
            return (0, 0, null);

        var recipeNames = new HashSet<string>(StringComparer.Ordinal);
        var chips = new List<EntityChipVm>();
        foreach (var profileName in profiles)
        {
            if (!refData.ItemsByTSysProfile.TryGetValue(profileName, out var items)) continue;
            foreach (var itemName in items)
            {
                if (!refData.RecipesByProducedItem.TryGetValue(itemName, out var recipes)) continue;
                foreach (var recipe in recipes)
                {
                    if (string.IsNullOrEmpty(recipe.InternalName)) continue;
                    if (!recipeNames.Add(recipe.InternalName!)) continue;
                    var reference = EntityRef.Recipe(recipe.InternalName!);
                    chips.Add(new EntityChipVm(
                        DisplayName: resolver.Resolve(reference),
                        IconId: 0,
                        Reference: reference,
                        IsNavigable: navigator.CanOpen(reference)));
                }
            }
        }

        if (chips.Count == 0) return (0, 0, null);

        chips.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        var popup = new ProvenancePopupViewModel(
            title: $"Recipes that can roll {powerInternalName}",
            sections: new List<ProvenancePopupSection>
            {
                new("Recipes that can roll this power", chips),
            });
        return (chips.Count, popup.TotalCount, popup);
    }
}
