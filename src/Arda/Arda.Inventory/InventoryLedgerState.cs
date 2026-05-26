using System.Text.Json.Serialization;
using Mithril.Shared.Character;

namespace Arda.Inventory;

/// <summary>
/// Serializable per-character snapshot of the inventory ledger.
/// Persisted at <c>characters/{slug}/inventory-ledger.json</c>.
/// </summary>
public sealed class InventoryLedgerState : IVersionedState<InventoryLedgerState>
{
    public const int Version = 1;
    public static int CurrentVersion => Version;

    public int SchemaVersion { get; set; }
    public Dictionary<long, PersistedItem> Entries { get; set; } = new();
    public DateTimeOffset? LastStorageReportTimestamp { get; set; }

    public static InventoryLedgerState Migrate(InventoryLedgerState loaded) => loaded;
}

/// <summary>
/// Plain POCO for JSON serialization. The live in-memory projection is
/// <see cref="InventoryItemModel"/> (INPC-observable).
/// </summary>
public sealed class PersistedItem
{
    public string InternalName { get; set; } = "";
    public string? DisplayName { get; set; }
    public int StackSize { get; set; }
    public int? TypeId { get; set; }
    public int? IconId { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastUpdatedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
    public InventorySource Sources { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(InventoryLedgerState))]
public partial class InventoryLedgerStateJsonContext : JsonSerializerContext;
