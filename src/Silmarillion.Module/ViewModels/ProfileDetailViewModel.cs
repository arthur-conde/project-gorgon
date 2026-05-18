using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using Mithril.Shared.Wpf.Query;

namespace Silmarillion.ViewModels;

/// <summary>One power row in a <see cref="ProfileDetailViewModel"/>'s pool list. The
/// scalar columns (<see cref="Name"/> / <see cref="InternalName"/> / <see cref="Skill"/>
/// / <see cref="Tiers"/>) are the surface the shared query system reflects + filters on
/// (mirrors Celebrimbor's <c>PooledAugmentOption</c>); <see cref="PowerLink"/> and
/// <see cref="TierText"/> are render-only. <see cref="Skill"/> is the power's <em>own</em>
/// skill — orthogonal to the pool name (the equipment family).</summary>
public sealed record TreasureProfilePowerRow(
    LinkVm PowerLink,
    string Name,
    string InternalName,
    string Skill,
    int Tiers,
    string TierText);

/// <summary>A clickable Power.Skill filter chip (tag-form Set-ref + Activate),
/// mirroring the <see cref="AbilityFilterSetRefVm"/> idiom.</summary>
public sealed class TreasureSkillFilterVm
{
    public TreasureSkillFilterVm(SetRefVm setRef, ICommand activate)
    {
        SetRef = setRef;
        Activate = activate;
    }

    public SetRefVm SetRef { get; }
    public ICommand Activate { get; }
}

/// <summary>
/// Lighter-pass detail projection of a Treasure-System profile / pool (#435 task 3).
/// A profile is a named pool of powers; the pool name is the <b>equipment family the
/// pool rolls onto</b>, NOT the contained powers' own skills — two orthogonal skill
/// axes. The copy makes that explicit and the list is filterable by
/// <c>Power.Skill</c>.
/// </summary>
public sealed partial class ProfileDetailViewModel : ObservableObject
{
    // The pool list reuses the shared query system (the cookbook step-6 / query-system
    // mandate), exactly mirroring Celebrimbor's AugmentPoolViewModel — which filters a
    // tsysprofiles pool the same way. The reflected RowSchema drives both the in-VM
    // QueryCompiler predicate and MithrilQueryBox's completion/highlighting; no bespoke
    // substring filter. QueryCompiler is used in-VM (not the QueryFilter attached
    // behaviour) because the ratified spec needs the post-filter count for CountSummary.
    private static readonly Dictionary<string, ColumnBinding> RowSchema =
        ColumnBindingHelper.BuildFromProperties(typeof(TreasureProfilePowerRow));

    /// <summary>Schema snapshot bound to <c>MithrilQueryBox.Schema</c>.</summary>
    public static IReadOnlyList<ColumnSchema> SchemaSnapshot { get; } =
        ColumnBindingHelper.ToSchema(RowSchema);

    private readonly List<TreasureProfilePowerRow> _allRows;

    public ProfileDetailViewModel(
        string profileName,
        IReferenceDataService refData,
        IReferenceNavigator navigator,
        IEntityNameResolver nameResolver,
        ICommand? openEntityCommand = null)
    {
        DisplayName = profileName;
        OpenEntityCommand = openEntityCommand;

        Explanation =
            $"“{profileName}” is a Treasure-System pool — the bag of powers a roll draws from. " +
            "The pool name is the equipment family the pool rolls onto, " +
            "not the contained powers' own skills: the list below carries powers from many skills.";

        // "Drawn into <name> family items" — a Skill-shaped Link. The Skills surface
        // isn't a browsable Silmarillion tab today, so this degrades (identical at
        // rest, click-copies) rather than dead-ending — exactly the G-c contract.
        var familyRef = EntityRef.Skill(profileName);
        FamilyLink = new LinkVm(
            DisplayName: nameResolver.Resolve(familyRef),
            Glyph: LinkGlyph.Skill,
            Reference: familyRef,
            IsNavigable: navigator.CanOpen(familyRef));

        _allRows = BuildRows(profileName, refData, nameResolver, navigator);
        PowerCount = _allRows.Count;

        SkillFilters = _allRows
            .Select(r => r.Skill)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .Select(skill => new TreasureSkillFilterVm(
                new SetRefVm(skill, MatchCount: null, IsActionable: true),
                new RelayCommand(() => ToggleSkillFilter(skill))))
            .ToList();

        VisiblePowerRows = new ObservableCollection<TreasureProfilePowerRow>(_allRows);

        Footer = FactFooterVm.Of(new FactFooterId("KEY", profileName, copyable: true));
    }

    /// <summary>The pool name — gold Cambria Fact-title and the copyable footer KEY.</summary>
    public string DisplayName { get; }

    /// <summary>Fact-body copy making the two orthogonal skill axes explicit.</summary>
    public string Explanation { get; }

    /// <summary>"Drawn into … family items" — Skill-shaped Link (degrades if Skills unshipped).</summary>
    public LinkVm FamilyLink { get; }

    /// <summary>Distinct count of powers in the pool (unfiltered).</summary>
    public int PowerCount { get; }

    /// <summary>Distinct <c>Power.Skill</c> values as clickable tag-form Set-ref filters.</summary>
    public IReadOnlyList<TreasureSkillFilterVm> SkillFilters { get; }

    /// <summary>The filtered pool list (virtualized in the view; ~270 rows for Sword).</summary>
    public ObservableCollection<TreasureProfilePowerRow> VisiblePowerRows { get; }

    /// <summary>
    /// Two-way bound to <c>MithrilQueryBox.QueryText</c>. The shared query language —
    /// <c>Skill = "Sword"</c>, <c>Tiers &gt;= 10</c>, bare text, AND/OR — over the
    /// reflected <see cref="SchemaSnapshot"/>; the Skill chips inject a clause into it.
    /// </summary>
    [ObservableProperty]
    private string _queryText = "";

    /// <summary>Last query compile error, surfaced under the box (mirrors the tab VMs
    /// / AugmentPoolViewModel); a malformed query shows everything, not nothing.</summary>
    [ObservableProperty]
    private string? _queryError;

    /// <summary>Subtitle reflecting the active filter ("270 powers" / "12 of 270 powers").</summary>
    public string CountSummary =>
        VisiblePowerRows.Count == PowerCount
            ? $"{PowerCount} powers"
            : $"{VisiblePowerRows.Count} of {PowerCount} powers";

    public ICommand? OpenEntityCommand { get; }

    /// <summary>G-a footer: copyable KEY = the pool name (its only identifier).</summary>
    public FactFooterVm Footer { get; }

    partial void OnQueryTextChanged(string value) => Recompute();

    private void ToggleSkillFilter(string skill)
    {
        // A skill chip is a shortcut that writes a real query clause; clicking the
        // active one again clears it. The query box stays the single filter surface.
        var clause = $"Skill = \"{skill}\"";
        QueryText = string.Equals(QueryText, clause, StringComparison.OrdinalIgnoreCase)
            ? ""
            : clause;
    }

    private Func<TreasureProfilePowerRow, bool>? CompilePredicate(string queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            QueryError = null;
            return null;
        }
        try
        {
            var compiled = QueryCompiler.Compile(queryText, RowSchema, caseSensitive: false);
            QueryError = null;
            return compiled is null ? null : row => compiled(row!);
        }
        catch (QueryException qex)
        {
            QueryError = qex.Message;
            // Malformed query → show everything; the error renders under the box.
            return null;
        }
    }

    private void Recompute()
    {
        var predicate = CompilePredicate(QueryText);
        IEnumerable<TreasureProfilePowerRow> filtered =
            predicate is null ? _allRows : _allRows.Where(predicate);

        VisiblePowerRows.Clear();
        foreach (var r in filtered) VisiblePowerRows.Add(r);
        OnPropertyChanged(nameof(CountSummary));
    }

    private static List<TreasureProfilePowerRow> BuildRows(
        string profileName,
        IReferenceDataService refData,
        IEntityNameResolver resolver,
        IReferenceNavigator navigator)
    {
        if (!refData.Profiles.TryGetValue(profileName, out var powerNames) || powerNames is null)
            return [];

        var rows = new List<TreasureProfilePowerRow>(powerNames.Count);
        foreach (var powerName in powerNames)
        {
            if (string.IsNullOrEmpty(powerName)) continue;
            refData.Powers.TryGetValue(powerName, out var power);

            var skillKey = power?.Skill;
            var skill = string.IsNullOrEmpty(skillKey)
                ? ""
                : (refData.Skills.TryGetValue(skillKey!, out var s) && !string.IsNullOrEmpty(s.DisplayName)
                    ? s.DisplayName
                    : skillKey!);
            var tierCount = power?.Tiers?.Count ?? 0;

            var reference = EntityRef.Power(powerName);
            var link = new LinkVm(
                DisplayName: resolver.Resolve(reference),
                Glyph: LinkGlyph.Power,
                Reference: reference,
                IsNavigable: navigator.CanOpen(reference));

            rows.Add(new TreasureProfilePowerRow(
                PowerLink: link,
                Name: link.DisplayName,
                InternalName: powerName,
                Skill: skill,
                Tiers: tierCount,
                TierText: tierCount == 1 ? "1 tier" : $"{tierCount} tiers"));
        }

        rows.Sort((a, b) =>
        {
            var s = string.Compare(a.Skill, b.Skill, StringComparison.OrdinalIgnoreCase);
            if (s != 0) return s;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return rows;
    }
}
