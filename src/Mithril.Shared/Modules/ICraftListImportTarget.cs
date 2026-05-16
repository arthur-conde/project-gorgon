namespace Mithril.Shared.Modules;

/// <summary>
/// One recipe to import into the craft list, identified by its reference-data
/// <c>InternalName</c> (the same key used by <c>RecipesByInternalName</c> and the
/// plain-text/deep-link share format). Used by the in-process import path so producing
/// modules (e.g. Elrond) don't take a hard reference on the craft-list module's domain types.
/// </summary>
public readonly record struct CraftListImportEntry(string RecipeInternalName, int Quantity);

/// <summary>
/// Optional target that handles incoming craft-list imports. Implemented by Celebrimbor to
/// activate its tab and run the existing Append/Replace/Cancel dialog. The router treats the
/// deep-link path as optional — if no module has registered a handler, list links are logged
/// and dropped; the in-process path is no-op when the target isn't registered.
/// </summary>
public interface ICraftListImportTarget
{
    /// <summary>
    /// Decodes <paramref name="base64UrlPayload"/> (the path segment from the deep link) and
    /// prompts the user to import it. All errors are logged and swallowed — callers are OS
    /// activation handlers that mustn't throw.
    /// </summary>
    void ImportFromLinkPayload(string base64UrlPayload);

    /// <summary>
    /// Imports recipes resolved in-process (no wire format). Activates the craft-list module's
    /// tab and runs the same Append/Replace/Cancel dialog as the deep-link path. Entries whose
    /// <see cref="CraftListImportEntry.RecipeInternalName"/> is unknown to reference data, or
    /// whose quantity is non-positive, are skipped (surfaced as dialog warnings). The
    /// <paramref name="source"/> label is shown in the dialog caption (e.g. "Elrond skills").
    /// All errors are logged and swallowed — callers mustn't throw.
    /// </summary>
    void ImportRecipes(IReadOnlyList<CraftListImportEntry> recipes, string source);
}
