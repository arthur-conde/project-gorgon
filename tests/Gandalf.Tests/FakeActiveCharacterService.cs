using Mithril.Shared.Character;
using Mithril.Shared.Storage;

namespace Gandalf.Tests;

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

    public void Refresh() { }

    public event EventHandler? ActiveCharacterChanged;
#pragma warning disable CS0067
    public event EventHandler? CharacterExportsChanged;
    public event EventHandler? StorageReportsChanged;
#pragma warning restore CS0067

    public void Dispose() { }
}
