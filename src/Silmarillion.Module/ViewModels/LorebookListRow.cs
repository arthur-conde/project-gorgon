using LorebookPoco = Mithril.Reference.Models.Misc.Lorebook;

namespace Silmarillion.ViewModels;

/// <summary>
/// Master-list row projection for the Lorebooks tab. Raw <see cref="LorebookPoco"/> doesn't
/// carry the resolved category display title (that lives in the
/// <c>lorebookinfo.json</c> sidecar) nor the derived "has body" flag, so — like Recipes —
/// the tab projects a row record rather than binding the POCO directly.
/// <para>
/// <see cref="InternalName"/> is the selection key (matches the
/// <see cref="Mithril.Shared.Reference.EntityRef.Lorebook(string)"/> factory and the
/// <c>LorebooksByInternalName</c> service contract). The <c>MithrilQueryBox</c> schema is
/// reflected from this record, so every queryable facet (category key, area key, has-body)
/// must be a public property here.
/// </para>
/// </summary>
public sealed record LorebookListRow(
    LorebookPoco Book,
    string InternalName,
    string Title,
    string CategoryDisplayTitle,
    string CategoryKey,
    string? AreaKey,
    bool HasText,
    string? LocationHint);
