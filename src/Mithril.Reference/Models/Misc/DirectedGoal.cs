using System.Collections.Generic;

namespace Mithril.Reference.Models.Misc;

/// <summary>
/// One entry from <c>directedgoals.json</c> — note that this file's top-level
/// shape is a JSON array (not the dictionary envelope used by most other
/// BundledData files). Goals are organised hierarchically: gates carry
/// <see cref="IsCategoryGate"/> = true and act as parents; sub-goals reference
/// their parent via <see cref="CategoryGateId"/>.
/// </summary>
public sealed class DirectedGoal
{
    public int Id { get; set; }
    public string? Label { get; set; }
    public string? LargeHint { get; set; }
    public string? SmallHint { get; set; }
    public string? Zone { get; set; }
    public bool? IsCategoryGate { get; set; }
    public int? CategoryGateId { get; set; }
    public IReadOnlyList<string>? ForRaces { get; set; }
}
