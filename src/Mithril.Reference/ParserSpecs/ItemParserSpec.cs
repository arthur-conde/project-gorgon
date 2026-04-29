using System.Collections.Generic;
using System.Linq;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Serialization;

namespace Mithril.Reference.ParserSpecs;

/// <summary>
/// <see cref="IParserSpec"/> for <c>items.json</c>. Items have no
/// polymorphic discriminator fields; the parser spec's
/// <see cref="EnumerateUnknowns"/> always yields empty.
/// </summary>
public sealed class ItemParserSpec : IParserSpec
{
    public string FileName => "items.json";

    /// <summary>Bundled file shipped 10730 items; floor leaves headroom for additions.</summary>
    public int MinimumEntryCount => 10500;

    public object Parse(string json) => ReferenceDeserializer.ParseItems(json);

    public int CountEntries(object parsed)
        => ((IReadOnlyDictionary<string, Item>)parsed).Count;

    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed)
        => Enumerable.Empty<UnknownReport>();
}
