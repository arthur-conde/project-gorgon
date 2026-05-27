using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Tracks vault interaction state and correlates paired verbs:
/// <list type="bullet">
///   <item><c>ProcessAddItem</c> + <c>ProcessRemoveFromStorageVault</c> → withdrawal (corrects stack size)</item>
///   <item><c>ProcessDeleteItem</c> + <c>ProcessAddToStorageVault</c> → deposit</item>
/// </list>
/// The vault session lifecycle is: <c>ProcessShowStorageVault</c> opens,
/// <c>ProcessEndInteraction</c> or zone change closes.
/// </summary>
internal sealed class Vault
{
    private readonly IDomainEventPublisher _bus;
    private readonly Inventory _inventory;

    // Vault session state
    private long? _vaultEntityId;
    private long? _storageId;

    // Pending correlation state — same-tick pairing (no temporal window needed)
    private (long InstanceId, string InternalName)? _pendingAdd;
    private (long InstanceId, string InternalName)? _pendingDelete;

    public Vault(IDomainEventPublisher bus, Inventory inventory)
    {
        _bus = bus;
        _inventory = inventory;
    }

    internal void Reset()
    {
        _vaultEntityId = null;
        _storageId = null;
        _pendingAdd = null;
        _pendingDelete = null;
    }

    /// <summary>
    /// Args format: <c>(entityId, storageId, label, flavorText, slotCount, ...)</c>
    /// </summary>
    internal void OnShowStorageVault(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
    {
        var tok = new ArgTokenizer(args, verb, sourceLog);
        tok.SkipOpen();

        var entityId = tok.NextLong();
        var storageId = tok.NextLong();
        var label = tok.NextQuotedSpan().ToString();
        tok.Skip(1); // flavorText
        var slotCount = tok.NextInt();

        _vaultEntityId = entityId;
        _storageId = storageId;
        _pendingAdd = null;
        _pendingDelete = null;

        _bus.Publish(new VaultOpened(entityId, storageId, label, slotCount, metadata));
    }

    /// <summary>
    /// Stashes the most recent <c>ProcessAddItem</c> as a withdrawal candidate.
    /// If <c>ProcessRemoveFromStorageVault</c> follows on the same tick, it correlates.
    /// </summary>
    internal void OnAddItem(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        var inner = SpanHelpers.StripParens(args);
        if (inner.IsEmpty)
            return;

        var nameEnd = inner.IndexOf('(');
        if (nameEnd <= 0)
            return;

        var nameSpan = inner[..nameEnd];
        var afterName = inner[(nameEnd + 1)..];

        var idEnd = afterName.IndexOf(')');
        if (idEnd <= 0)
            return;

        if (!long.TryParse(afterName[..idEnd], out var instanceId))
            return;

        _pendingAdd = (instanceId, nameSpan.ToString());
    }

    /// <summary>
    /// Stashes the most recent <c>ProcessDeleteItem</c> as a deposit candidate.
    /// If <c>ProcessAddToStorageVault</c> follows on the same tick, it correlates.
    /// </summary>
    internal void OnDeleteItem(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        var inner = SpanHelpers.StripParens(args);
        if (inner.IsEmpty)
            return;

        if (!long.TryParse(inner, out var instanceId))
            return;

        // Look up the internal name from inventory state (it was just removed by
        // Inventory.OnDeleteItem which runs before us in dispatch order)
        // If unavailable, stash with empty name — deposit event still carries the ID
        var name = _inventory.Items.TryGetValue(instanceId, out var entry)
            ? entry.InternalName
            : string.Empty;

        // The item is already removed from Inventory by the time we run, so we
        // can't look it up. Instead, parse the instanceId and rely on
        // ProcessAddToStorageVault providing the name via its args.
        _pendingDelete = (instanceId, name);
    }

    /// <summary>
    /// Args format: <c>(entityId, -1, instanceId, stackCount)</c>
    /// Correlates with the preceding <c>ProcessAddItem</c>, corrects inventory stack size,
    /// and emits <see cref="VaultWithdrawal"/>.
    /// </summary>
    internal void OnRemoveFromStorageVault(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
    {
        var tok = new ArgTokenizer(args, verb, sourceLog);
        tok.SkipOpen();

        var entityId = tok.NextLong();
        tok.NextLong(); // always -1
        var instanceId = tok.NextLong();
        var stackCount = tok.NextInt();

        // Correct the stack size on the inventory entry that ProcessAddItem just set to 1
        _inventory.CorrectStackSize(instanceId, stackCount, metadata);

        var vaultEntityId = _vaultEntityId ?? entityId;
        var internalName = string.Empty;

        if (_pendingAdd is { } pending && pending.InstanceId == instanceId)
        {
            internalName = pending.InternalName;
            _pendingAdd = null;
        }
        else if (_inventory.Items.TryGetValue(instanceId, out var entry))
        {
            internalName = entry.InternalName;
        }

        _bus.Publish(new VaultWithdrawal(instanceId, internalName, stackCount, vaultEntityId, metadata));
    }

    /// <summary>
    /// Args format: <c>(entityId, -1, slotIndex, InternalName(instanceId))</c>
    /// Correlates with the preceding <c>ProcessDeleteItem</c> and emits <see cref="VaultDeposit"/>.
    /// </summary>
    internal void OnAddToStorageVault(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
    {
        var tok = new ArgTokenizer(args, verb, sourceLog);
        tok.SkipOpen();

        var entityId = tok.NextLong();
        tok.NextLong(); // always -1
        var slotIndex = tok.NextInt();

        // Fourth arg is InternalName(instanceId) — same format as ProcessAddItem's first arg
        var nameAndId = tok.NextTokenSpan();
        var parenIdx = nameAndId.IndexOf('(');
        if (parenIdx <= 0)
            return;

        var internalName = nameAndId[..parenIdx].ToString();
        var idSpan = nameAndId[(parenIdx + 1)..];
        var closeIdx = idSpan.IndexOf(')');
        if (closeIdx > 0)
            idSpan = idSpan[..closeIdx];

        if (!long.TryParse(idSpan, System.Globalization.CultureInfo.InvariantCulture, out var instanceId))
            return;

        // Use the pending delete's name if it matches, otherwise use what we parsed
        if (_pendingDelete is { } pending && pending.InstanceId == instanceId && pending.InternalName.Length > 0)
            internalName = pending.InternalName;

        _pendingDelete = null;

        var vaultEntityId = _vaultEntityId ?? entityId;
        _bus.Publish(new VaultDeposit(instanceId, internalName, vaultEntityId, slotIndex, metadata));
    }
}
