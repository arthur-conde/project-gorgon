using Mithril.Shared.Character;
using Mithril.Shared.Storage;

namespace Legolas.Tests;

/// <summary>
/// Minimal <see cref="IActiveCharacterService"/> double for the #497
/// character-pin tests. Only <see cref="ActiveCharacterName"/> +
/// <see cref="ActiveCharacterChanged"/> are exercised; the rest are inert
/// stubs.
/// </summary>
public sealed class FakeActiveCharacterService : IActiveCharacterService
{
    private string? _name;

    /// <summary>Set the active character name and raise
    /// <see cref="ActiveCharacterChanged"/> (mimics a login / character switch).</summary>
    public void SetName(string? name)
    {
        _name = name;
        ActiveCharacterChanged?.Invoke(this, EventArgs.Empty);
    }

    public string? ActiveCharacterName => _name;

    public IReadOnlyList<CharacterSnapshot> Characters => Array.Empty<CharacterSnapshot>();
    public IReadOnlyList<ReportFileInfo> StorageReports => Array.Empty<ReportFileInfo>();
    public string? ActiveServer => null;
    public CharacterSnapshot? ActiveCharacter => null;
    public ReportFileInfo? ActiveStorageReport => null;
    public StorageReport? ActiveStorageContents => null;

    public event EventHandler? ActiveCharacterChanged;
    public event EventHandler? CharacterExportsChanged { add { } remove { } }
    public event EventHandler? StorageReportsChanged { add { } remove { } }

    public void SetActiveCharacter(string name, string server) => SetName(name);
    public void Refresh() { }
    public void Dispose() { }
}
