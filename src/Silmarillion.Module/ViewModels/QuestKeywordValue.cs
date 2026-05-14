using Mithril.Reference;

namespace Silmarillion.ViewModels;

/// <summary>
/// Lightweight wrapper that lets a quest's keyword tag (e.g. <c>"MainStory"</c>,
/// <c>"DailyQuest"</c>, <c>"Tutorial"</c>) participate in the query engine's
/// collection-<c>CONTAINS</c> path (see <see cref="IQueryStringValue"/>, shipped
/// in PR #261). Mirror of <see cref="IngredientKeywordValue"/> / <see cref="NpcServiceTypeValue"/>
/// for the quest-keyword pivot — powers <c>Keywords CONTAINS "MainStory"</c>.
/// </summary>
public sealed record QuestKeywordValue(string Tag) : IQueryStringValue
{
    public string QueryStringValue => Tag;
}
