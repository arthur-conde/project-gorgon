using System.Text;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;

namespace Silmarillion.ViewModels;

/// <summary>
/// Projects a recipe's polymorphic <see cref="Recipe.OtherRequirements"/> to the
/// player-facing rows used by <see cref="RecipeDetailViewModel"/>.
/// <para>
/// This is the <em>display</em> half of the parity the planner deliberately punts on:
/// <c>docs/planner-recipe-field-consumption.md</c> records that <c>CrossSkillPlanner</c>
/// does not auto-gate on the user-asserted unlocks (<c>PetCount</c>, <c>HasHands</c>, …)
/// nor on the time/RNG-cyclical gates (<c>Weather</c>, <c>MoonPhase</c>, …). That punt is
/// only sound if the player can <em>see</em> the gate somewhere — so the gates the planner
/// won't pursue are exactly the ones this projector must surface.
/// </para>
/// <para>
/// Shape mirrors <c>StorageVaultDetailViewModel.BuildRequirements</c> — a flat
/// <see cref="string"/> line list for the (mostly non-navigable) gates plus a separate
/// 1:1 <see cref="EntityChipVm"/> list for the recipe-referencing kinds
/// (<c>RecipeKnown</c> / cross-recipe <c>RecipeUsed</c>) which are real cross-links into
/// the same Recipes tab. The heavier intent-bucketing <c>QuestDetailProjector</c> uses is
/// deliberately not copied: the recipe requirement set is tiny (≤19 per recipe across
/// ~42 recipes) and homogeneous enough that a flat list reads fine.
/// </para>
/// </summary>
public static class RecipeRequirementProjector
{
    /// <summary>
    /// Project <paramref name="requirements"/> into (plain readable lines, navigable
    /// recipe-cross-link chips). <paramref name="ownerInternalName"/> is the recipe these
    /// requirements belong to — used to recognise a self-referential <c>RecipeUsed</c>
    /// (the per-character craft cap, the WeatherWitching litany) and render it as a
    /// count line rather than a dead self-chip.
    /// </summary>
    public static (IReadOnlyList<string> Lines, IReadOnlyList<EntityChipVm> RecipeChips) Build(
        IReadOnlyList<RecipeRequirement>? requirements,
        string? ownerInternalName,
        IReferenceNavigator navigator,
        IEntityNameResolver resolver,
        IReadOnlyDictionary<string, string> strings)
    {
        if (requirements is null || requirements.Count == 0)
            return (Array.Empty<string>(), Array.Empty<EntityChipVm>());

        var lines = new List<string>();
        var chips = new List<EntityChipVm>();

        EntityChipVm RecipeChip(string internalName)
        {
            var reference = EntityRef.Recipe(internalName);
            return new EntityChipVm(
                DisplayName: resolver.Resolve(reference),
                IconId: 0,
                Reference: reference,
                IsNavigable: navigator.CanOpen(reference));
        }

        foreach (var req in requirements)
        {
            switch (req)
            {
                case AlwaysFailRequirement:
                    // The planner's hard-exclude case (ImproveProphesied* etc.) — the recipe
                    // can never succeed despite any advertised XP. Say so plainly.
                    lines.Add("This recipe can never be completed (unavailable placeholder).");
                    break;

                case RecipeKnownRequirement r when !string.IsNullOrEmpty(r.Recipe):
                    chips.Add(RecipeChip(r.Recipe!));
                    break;

                case RecipeUsedRequirement u when !string.IsNullOrEmpty(u.Recipe):
                    if (string.Equals(u.Recipe, ownerInternalName, StringComparison.Ordinal))
                    {
                        // Self-referential RecipeUsed{self, n} ⇒ a per-character lifetime
                        // cap of n+1 crafts (the planner folds this into RemainingCraftBudget
                        // alongside MaxUses). A self-chip would be a dead loop, so render the
                        // cap as a line instead.
                        lines.Add(u.MaxTimesUsed is { } n
                            ? $"Limited to {n + 1} craft{(n + 1 == 1 ? "" : "s")} per character."
                            : "Limited per-character craft count.");
                    }
                    else
                    {
                        chips.Add(RecipeChip(u.Recipe!));
                    }
                    break;

                case WeatherRequirement w:
                    lines.Add(w.ClearSky switch
                    {
                        true => "Only when the sky is clear.",
                        false => "Only when the weather is overcast.",
                        null => "Weather-dependent.",
                    });
                    break;

                case MoonPhaseRecipeRequirement m when !string.IsNullOrEmpty(m.MoonPhase):
                    lines.Add($"Only during the {Humanise(m.MoonPhase!).ToLowerInvariant()} moon.");
                    break;

                case FullMoonRecipeRequirement:
                    lines.Add("Only during the full moon.");
                    break;

                case TimeOfDayRecipeRequirement t:
                    lines.Add((t.MinHour, t.MaxHour) switch
                    {
                        ({ } lo, { } hi) => $"Only between {lo:00}:00 and {hi:00}:00 in-game time.",
                        ({ } lo, null) => $"Only after {lo:00}:00 in-game time.",
                        (null, { } hi) => $"Only before {hi:00}:00 in-game time.",
                        _ => "Time-of-day dependent.",
                    });
                    break;

                case IsHardcoreRequirement:
                    lines.Add("Hardcore characters only.");
                    break;

                case IsLycanthropeRequirement:
                    lines.Add("Werewolf characters only.");
                    break;

                case HasHandsRequirement:
                    lines.Add("Requires hands (not available in animal form).");
                    break;

                case HasGuildHallRequirement:
                    lines.Add("Requires a guild hall.");
                    break;

                case InGraveyardRequirement:
                    lines.Add("Must be in a graveyard.");
                    break;

                case PetCountRecipeRequirement p:
                    lines.Add(DescribePetCount(p, strings));
                    break;

                case HasEffectKeywordRecipeRequirement h when !string.IsNullOrEmpty(h.Keyword):
                    lines.Add(h.MinCount is > 1
                        ? $"Requires the effect “{Humanise(h.Keyword!)}” ×{h.MinCount}."
                        : $"Requires the effect “{Humanise(h.Keyword!)}”.");
                    break;

                case EquipmentSlotEmptyRecipeRequirement e when !string.IsNullOrEmpty(e.Slot):
                    lines.Add($"The {Humanise(e.Slot!).ToLowerInvariant()} equipment slot must be empty.");
                    break;

                case AppearanceRecipeRequirement a when !string.IsNullOrEmpty(a.Appearance):
                    lines.Add($"Requires appearance: {Humanise(a.Appearance!)}.");
                    break;

                case EntityPhysicalStateRecipeRequirement s when s.AllowedStates is { Count: > 0 }:
                    lines.Add($"Requires physical state: {string.Join(", ", s.AllowedStates!)}.");
                    break;

                case DruidEventStateRequirement d when d.DisallowedStates is { Count: > 0 }:
                    lines.Add($"Not available during druid event: {string.Join(", ", d.DisallowedStates!)}.");
                    break;

                case EntitiesNearRequirement n:
                    if (!string.IsNullOrEmpty(n.ErrorMsg))
                        lines.Add(n.ErrorMsg!);
                    else
                    {
                        var what = string.IsNullOrEmpty(n.EntityTypeTag) ? "entities" : Humanise(n.EntityTypeTag!);
                        lines.Add($"Requires {n.MinCount ?? 1} {what} nearby.");
                    }
                    break;

                case UnknownRecipeRequirement u:
                    // Graceful degrade for a future PG-added discriminator — surface it so
                    // it's diagnosable, behind a clearly-noise-filtered label (never blank,
                    // never a crash). Same contract as StorageVault's unknown handling.
                    lines.Add(string.IsNullOrEmpty(u.DiscriminatorValue)
                        ? "(unrecognised requirement)"
                        : $"(unrecognised requirement: {u.DiscriminatorValue})");
                    break;

                default:
                    // A known subclass with an empty payload (defensive — RecipeKnown /
                    // RecipeUsed with no Recipe, MoonPhase with no phase). Skip silently
                    // rather than emit a blank line.
                    break;
            }
        }

        return (lines, chips);
    }

    /// <summary>
    /// Phrase a pet-count gate. <c>MinCount</c>/<c>MaxCount</c> are bounds, not a required
    /// quantity — the corpus only carries upper bounds (<c>SummonedBakingBread</c> ≤4,
    /// <c>StorageCrateDruid</c> ≤0), so "Requires N …" would be doubly wrong (it's a cap,
    /// and N=0 means *none allowed*). The word "pet" is appended only when the resolved
    /// kind doesn't already end in it, killing the "… Cow Pet pets" redundancy.
    /// <para>
    /// A <c>PetTypeTag</c> is an NPC/monster entity in PG's data model, so its real
    /// in-game name lives in <c>strings_all</c> under <c>npc_&lt;tag&gt;_Name</c>
    /// ("SummonedBakingBread" → "Rising Dough"). Resolve through there per the
    /// id→display-name convention; <see cref="Humanise"/> is only the fallback when the
    /// tag has no string entry (these aren't in <c>npcs.json</c>, so the
    /// <see cref="IEntityNameResolver"/>'s NPC path can't help).
    /// </para>
    /// </summary>
    private static string DescribePetCount(PetCountRecipeRequirement p, IReadOnlyDictionary<string, string> strings)
    {
        string kind;
        if (string.IsNullOrEmpty(p.PetTypeTag))
            kind = "pet";
        else if (strings.TryGetValue($"npc_{p.PetTypeTag}_Name", out var resolved) && !string.IsNullOrEmpty(resolved))
            kind = resolved;
        else
            kind = Humanise(p.PetTypeTag!);
        var kindIsPetNoun =
            kind.EndsWith("pet", StringComparison.OrdinalIgnoreCase)
            || kind.EndsWith("pets", StringComparison.OrdinalIgnoreCase);
        string Noun(int n) => kindIsPetNoun ? kind : $"{kind} pet{(n == 1 ? "" : "s")}";

        var min = p.MinCount;
        var max = p.MaxCount;

        // Max 0 with no floor ⇒ the pet is disallowed entirely.
        if (max is 0 && min is null or 0)
            return $"Must not own any {kind}.";

        return (min, max) switch
        {
            ({ } a, { } b) when a == b => $"Requires exactly {a} {Noun(a)}.",
            ({ } a, { } b) => $"Requires {a}–{b} {Noun(b)}.",
            ({ } a, null) => $"Requires at least {a} {Noun(a)}.",
            (null, { } b) => $"Requires at most {b} {Noun(b)}.",
            _ => kindIsPetNoun ? $"Requires a {kind}." : $"Requires a {kind} pet.",
        };
    }

    /// <summary>
    /// Insert spaces at CamelCase / underscore word boundaries so a raw identifier reads
    /// as a phrase ("WaningCrescent" → "Waning Crescent", "Main_Hand" → "Main Hand").
    /// Matches the <c>QuestDetailProjector.SplitCamelCase</c> contract; kept local so the
    /// two projectors stay independent (the cross-domain split noted on
    /// <see cref="RecipeRequirement"/>).
    /// </summary>
    private static string Humanise(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        if (input.Length < 2) return input;
        var sb = new StringBuilder(input.Length + 4);
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
}
