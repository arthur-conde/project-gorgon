using PlayerTitlePoco = Mithril.Reference.Models.Misc.PlayerTitle;

namespace Silmarillion.ViewModels;

/// <summary>
/// Master-list row projection for the PlayerTitles tab. The raw
/// <see cref="PlayerTitlePoco"/> carries its label as
/// <c>&lt;color=…&gt;…&lt;/color&gt;</c>-wrapped markup and exposes no clean
/// display string or derived obtainability flag, so — like Recipes / Lorebooks —
/// the tab projects a row record rather than binding the POCO directly.
/// <para>
/// <see cref="EnvelopeKey"/> (<c>"Title_5018"</c>) is the selection key: the
/// PlayerTitle POCO carries <i>no</i> InternalName, so the envelope key is the
/// only identifier, and it is what <see cref="Mithril.Shared.Reference.EntityRef.PlayerTitle(string)"/>
/// and the kind target / deep-link route resolve against. The <c>MithrilQueryBox</c>
/// schema is reflected from this record, so every queryable facet
/// (<see cref="IsObtainable"/>, <see cref="HasTooltip"/>,
/// <see cref="AccountWide"/>, <see cref="SoulWide"/>) must be a public property here.
/// </para>
/// </summary>
public sealed record PlayerTitleListRow(
    PlayerTitlePoco Title,
    string EnvelopeKey,
    string DisplayTitle,
    bool HasTooltip,
    bool IsObtainable,
    bool AccountWide,
    bool SoulWide);
