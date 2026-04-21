namespace Gorgon.Shared.Character;

/// <summary>
/// Discovers and parses character export JSON files from the game's Reports directory.
/// Watches for new/updated files and raises <see cref="CharactersChanged"/>.
/// </summary>
public interface ICharacterDataService : IDisposable
{
    /// <summary>All discovered character snapshots, most recent export first.</summary>
    IReadOnlyList<CharacterSnapshot> Characters { get; }

    /// <summary>The currently selected character for analysis. Null if none selected.</summary>
    CharacterSnapshot? ActiveCharacter { get; }

    /// <summary>Select a character by name + server as the active character.</summary>
    void SetActiveCharacter(string name, string server);

    /// <summary>Re-scan the Reports directory for new/updated exports.</summary>
    void Refresh();

    /// <summary>Raised when the character list changes (new file detected, re-scan, etc.).</summary>
    event EventHandler? CharactersChanged;
}
