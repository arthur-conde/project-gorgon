namespace Mithril.Shared.Reference;

/// <summary>
/// One entry from <c>areas.json</c>. Maps an area's internal key (e.g. <c>"AreaSerbule"</c>)
/// to its display names. <see cref="ShortFriendlyName"/> falls back to <see cref="FriendlyName"/>
/// when the JSON omits it.
/// </summary>
public sealed record AreaEntry(string Key, string FriendlyName, string ShortFriendlyName);
