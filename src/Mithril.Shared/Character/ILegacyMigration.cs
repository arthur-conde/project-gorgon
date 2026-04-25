namespace Mithril.Shared.Character;

/// <summary>
/// Pluggable legacy-path migration for <see cref="PerCharacterStore{T}"/>. When the store
/// is asked to load a character's file and finds none, it delegates to this migration
/// before falling back to <c>new T()</c>. The migration reads the module's old flat-file
/// layout (e.g. <c>%LocalAppData%/Mithril/Pippin/gourmand-state.json</c>) and hands the
/// parsed state back; the store then writes it to the new per-character path and deletes
/// the legacy file (and its empty parent dir).
/// </summary>
public interface ILegacyMigration<T> where T : class, new()
{
    /// <summary>
    /// Attempt to produce a migrated <typeparamref name="T"/> for the given character.
    /// </summary>
    /// <param name="character">Active character name.</param>
    /// <param name="server">Active server.</param>
    /// <param name="migrated">Parsed state, populated only when the method returns <c>true</c>.</param>
    /// <param name="legacyPath">Absolute path of the legacy file that was read; the caller
    /// deletes it after the new-path write succeeds. Empty string if none existed.</param>
    bool TryMigrate(string character, string server, out T migrated, out string legacyPath);
}
