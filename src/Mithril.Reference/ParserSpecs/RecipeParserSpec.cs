using System.Collections.Generic;
using Mithril.Reference.Models;
using Mithril.Reference.Models.Recipes;
using Mithril.Reference.Serialization;

namespace Mithril.Reference.ParserSpecs;

/// <summary>
/// <see cref="IParserSpec"/> for <c>recipes.json</c>. The validation theory
/// discovers this automatically via reflection.
/// </summary>
public sealed class RecipeParserSpec : IParserSpec
{
    public string FileName => "recipes.json";

    /// <summary>Bundled file shipped 4427 recipes; floor leaves headroom for additions.</summary>
    public int MinimumEntryCount => 4300;

    public object Parse(string json) => ReferenceDeserializer.ParseRecipes(json);

    public int CountEntries(object parsed)
        => ((IReadOnlyDictionary<string, Recipe>)parsed).Count;

    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed)
    {
        var recipes = (IReadOnlyDictionary<string, Recipe>)parsed;
        foreach (var pair in recipes)
        {
            var key = pair.Key;
            var recipe = pair.Value;

            if (recipe.OtherRequirements is { } reqs)
                for (var i = 0; i < reqs.Count; i++)
                {
                    if (reqs[i] is IUnknownDiscriminator u)
                        yield return new UnknownReport(
                            $"{key}/OtherRequirements[{i}]",
                            u.DiscriminatorValue,
                            nameof(RecipeRequirement));
                }
        }
    }
}
