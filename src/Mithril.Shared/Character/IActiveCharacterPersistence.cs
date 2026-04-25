namespace Mithril.Shared.Character;

/// <summary>
/// Minimal contract for persisting the active character across app restarts.
/// Implemented by the shell's settings object so <see cref="ActiveCharacterService"/>
/// can survive restarts without depending on shell types directly.
/// </summary>
public interface IActiveCharacterPersistence
{
    string? ActiveCharacterName { get; set; }
    string? ActiveServer { get; set; }
}
