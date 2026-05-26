using Arda.Dispatch;
using Mithril.GameReports;
using Mithril.Shared.Character;

namespace Arda.Inventory.Tests;

internal sealed class TestDomainEventBus : IDomainEventSubscriber
{
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();

    public IDisposable Subscribe<T>(Action<T> handler) where T : struct
    {
        var type = typeof(T);
        if (!_handlers.TryGetValue(type, out var list))
        {
            list = [];
            _handlers[type] = list;
        }
        list.Add(handler);
        return new Subscription(this, type, handler);
    }

    public void Publish<T>(T domainEvent) where T : struct
    {
        if (!_handlers.TryGetValue(typeof(T), out var list)) return;
        foreach (var h in list.ToArray())
            ((Action<T>)h)(domainEvent);
    }

    private sealed class Subscription(TestDomainEventBus bus, Type type, Delegate handler) : IDisposable
    {
        public void Dispose()
        {
            if (bus._handlers.TryGetValue(type, out var list))
                list.Remove(handler);
        }
    }
}

internal sealed class FakeActiveCharacterService : IActiveCharacterService
{
    public IReadOnlyList<CharacterSnapshot> Characters { get; set; } = [];
    public IReadOnlyList<ReportFileInfo> StorageReports { get; set; } = [];
    public string? ActiveCharacterName { get; set; }
    public string? ActiveServer { get; set; }
    public CharacterSnapshot? ActiveCharacter { get; set; }
    public ReportFileInfo? ActiveStorageReport { get; set; }
    public StorageReport? ActiveStorageContents { get; set; }

    public void SetActiveCharacter(string name, string server)
    {
        ActiveCharacterName = name;
        ActiveServer = server;
        ActiveCharacterChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Refresh() { }
    public void RaiseStorageReportsChanged() => StorageReportsChanged?.Invoke(this, EventArgs.Empty);

    public event EventHandler? ActiveCharacterChanged;
    public event EventHandler? CharacterExportsChanged;
    public event EventHandler? StorageReportsChanged;

    public void Dispose() { }

    internal void SuppressWarnings()
    {
        CharacterExportsChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// In-memory <see cref="ILedgerStateView"/> replacement. No file I/O.
/// </summary>
internal sealed class FakeLedgerStateView : ILedgerStateView
{
    public InventoryLedgerState? Current { get; set; } = new();
    public int SaveCount { get; private set; }

    public void Save() => SaveCount++;

    public void SwitchCharacter(InventoryLedgerState? newState)
    {
        Current = newState;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? CurrentChanged;
}
