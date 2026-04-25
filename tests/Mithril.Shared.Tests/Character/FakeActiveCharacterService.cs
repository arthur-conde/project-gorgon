using Mithril.Shared.Character;
using Mithril.Shared.Storage;

namespace Mithril.Shared.Tests.Character;

/// <summary>Mutable test double for <see cref="IActiveCharacterService"/>.</summary>
internal sealed class FakeActiveCharacterService : IActiveCharacterService
{
    public IReadOnlyList<CharacterSnapshot> Characters { get; set; } = [];
    public IReadOnlyList<ReportFileInfo> StorageReports { get; set; } = [];

    public string? ActiveCharacterName { get; private set; }
    public string? ActiveServer { get; private set; }
    public CharacterSnapshot? ActiveCharacter { get; set; }
    public ReportFileInfo? ActiveStorageReport { get; set; }
    public StorageReport? ActiveStorageContents { get; set; }

    public void SetActiveCharacter(string name, string server)
    {
        if (ActiveCharacterName == name && ActiveServer == server) return;
        ActiveCharacterName = name;
        ActiveServer = server;
        ActiveCharacterChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (ActiveCharacterName is null && ActiveServer is null) return;
        ActiveCharacterName = null;
        ActiveServer = null;
        ActiveCharacterChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Refresh() { }

    public event EventHandler? ActiveCharacterChanged;
    public event EventHandler? CharacterExportsChanged;
    public event EventHandler? StorageReportsChanged;

    public void RaiseCharacterExportsChanged() => CharacterExportsChanged?.Invoke(this, EventArgs.Empty);
    public void RaiseStorageReportsChanged() => StorageReportsChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose() { }
}
