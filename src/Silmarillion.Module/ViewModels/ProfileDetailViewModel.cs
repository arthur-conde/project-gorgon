using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;

namespace Silmarillion.ViewModels;

/// <summary>One power row in a <see cref="ProfileDetailViewModel"/>'s pool list: the
/// navigable power Link plus the power's own <see cref="SkillText"/> and tier count as
/// inert Fact. <see cref="SkillText"/> is the power's <em>own</em> skill — orthogonal
/// to the pool name (which is the equipment family).</summary>
public sealed record TreasureProfilePowerRow(LinkVm PowerLink, string PowerInternalName, string SkillText, string TierText);

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
            .Select(r => r.SkillText)
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

    /// <summary>Free-text pool filter (matches power name or its own skill, case-insensitive).</summary>
    [ObservableProperty]
    private string _filterText = "";

    /// <summary>Subtitle reflecting the active filter ("270 powers" / "12 of 270 powers").</summary>
    public string CountSummary =>
        VisiblePowerRows.Count == PowerCount
            ? $"{PowerCount} powers"
            : $"{VisiblePowerRows.Count} of {PowerCount} powers";

    public ICommand? OpenEntityCommand { get; }

    /// <summary>G-a footer: copyable KEY = the pool name (its only identifier).</summary>
    public FactFooterVm Footer { get; }

    partial void OnFilterTextChanged(string value) => Recompute();

    private void ToggleSkillFilter(string skill)
    {
        // Click a skill chip to filter to it; click again (or any chip whose skill is
        // already the active filter) to clear — a lightweight one-axis toggle.
        FilterText = string.Equals(FilterText, skill, StringComparison.OrdinalIgnoreCase)
            ? ""
            : skill;
    }

    private void Recompute()
    {
        var q = FilterText?.Trim();
        IEnumerable<TreasureProfilePowerRow> filtered = _allRows;
        if (!string.IsNullOrEmpty(q))
        {
            filtered = _allRows.Where(r =>
                r.PowerLink.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.PowerInternalName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.SkillText.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

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
            var skillText = string.IsNullOrEmpty(skillKey)
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
                PowerInternalName: powerName,
                SkillText: skillText,
                TierText: tierCount == 1 ? "1 tier" : $"{tierCount} tiers"));
        }

        rows.Sort((a, b) =>
        {
            var s = string.Compare(a.SkillText, b.SkillText, StringComparison.OrdinalIgnoreCase);
            if (s != 0) return s;
            return string.Compare(a.PowerLink.DisplayName, b.PowerLink.DisplayName, StringComparison.OrdinalIgnoreCase);
        });
        return rows;
    }
}
