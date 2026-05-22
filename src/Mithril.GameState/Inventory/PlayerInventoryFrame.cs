namespace Mithril.GameState.Inventory;

/// <summary>
/// World-simulator frame payload for the Player.log inventory folder
/// (<see cref="PlayerInventoryStateService"/>) — #602. A sibling
/// <see cref="Producers.PlayerInventoryFrameProducer"/> reads classified
/// LocalPlayer log envelopes, parses inventory-relevant verbs, and emits one
/// of the subtypes below for every relevant line.
///
/// <para>Closed hierarchy keeps the folder's switch exhaustive at compile
/// time. Same shape as <see cref="Mithril.GameState.Skills.SkillFrame"/>
/// (Phase 1 canonical example).</para>
/// </summary>
public abstract record PlayerInventoryFrame
{
    private protected PlayerInventoryFrame() { }
}

/// <summary>
/// <c>ProcessAddItem(InternalName(instanceId), slot, bool)</c>. The Player.log
/// half does not carry a stack size — quantity composition is the view layer's
/// concern.
/// </summary>
public sealed record PlayerInventoryAddFrame(long InstanceId, string InternalName) : PlayerInventoryFrame;

/// <summary>
/// <c>ProcessDeleteItem(instanceId)</c>. The folder retains the
/// id-to-<c>InternalName</c> mapping after the remove so late lookups still
/// resolve.
/// </summary>
public sealed record PlayerInventoryRemoveFrame(long InstanceId) : PlayerInventoryFrame;

/// <summary>
/// <c>ProcessUpdateItemCode(instanceId, code, _)</c> — high 16 bits of
/// <c>code</c> decode to <c>stackSize - 1</c>. Authoritative stack-size update
/// for an already-tracked instance.
/// </summary>
public sealed record PlayerInventoryUpdateItemCodeFrame(long InstanceId, long Code) : PlayerInventoryFrame;

/// <summary>
/// <c>ProcessRemoveFromStorageVault(_, _, instanceId, stackSize)</c>.
/// Authoritative stack-size update for the bag-side id when the player
/// withdraws from a storage vault.
/// </summary>
public sealed record PlayerInventoryVaultWithdrawFrame(long InstanceId, int StackSize) : PlayerInventoryFrame;
