using System.Collections.Generic;

namespace Mithril.Reference.Models.Sources;

/// <summary>
/// One entry in the top-level dictionary of a sources_*.json file
/// (e.g. the value at <c>"item_5010"</c> in sources_items.json). Wraps the
/// list of polymorphic source entries describing where the keyed thing is
/// obtainable.
/// </summary>
public sealed class SourceEnvelope
{
    /// <summary>Lowercase property name to match the JSON literally.</summary>
    public IReadOnlyList<SourceEntry>? entries { get; set; }
}
