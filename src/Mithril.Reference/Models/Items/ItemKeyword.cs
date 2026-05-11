namespace Mithril.Reference.Models.Items;

/// <summary>
/// A parsed keyword from an item. Raw JSON form is <c>"VegetarianDish=84"</c>
/// where <see cref="Tag"/> is <c>"VegetarianDish"</c> and <see cref="Quality"/>
/// is 84. Keywords without <c>=N</c> have <see cref="Quality"/> = 0.
/// </summary>
/// <remarks>
/// This is one of two deliberate deviations from the "property shapes match JSON
/// exactly" invariant — the JSON ships these as plain strings. See
/// <c>docs/mithril-reference-shape-quirks.md</c> for the rationale.
/// </remarks>
public sealed record ItemKeyword(string Tag, int Quality);
