using Mithril.Reference;

namespace Silmarillion.ViewModels;

/// <summary>
/// Lightweight wrapper that lets an NPC's flattened service-type set
/// participate in the query engine's collection-<c>CONTAINS</c> path. Exposes the
/// raw service <see cref="Type"/> (e.g. <c>"Store"</c>, <c>"Training"</c>,
/// <c>"Barter"</c>) as the queryable string. Mirror of <see cref="IngredientKeywordValue"/>
/// for the NPC service-type pivot — powers the NPCs tab's
/// <c>ServiceTypes CONTAINS "Store"</c> query.
/// </summary>
public sealed record NpcServiceTypeValue(string Type) : IQueryStringValue
{
    public string QueryStringValue => Type;
}
