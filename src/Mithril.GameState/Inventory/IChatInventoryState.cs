namespace Mithril.GameState.Inventory;

/// <summary>
/// ChatWorld half of the post-split inventory (#602). A name-keyed time-series
/// of stack-size observations folded from chat's
/// <c>[Status] X xN added to inventory.</c> channel. The view layer's
/// correlator pairs these observations with matching
/// <see cref="PlayerInventoryAdded"/> emissions from the PlayerWorld bus.
///
/// <para>This is a recorder, not a ledger — chat carries no removal signal,
/// so the folder only ever appends observations. Cross-source composition
/// (instance id ↔ display name) lives in <see cref="IInventoryView"/>.</para>
///
/// <para><b>Naming.</b> Follows #657 — folder interfaces take the form
/// <c>I&lt;World&gt;&lt;Domain&gt;State</c>.</para>
/// </summary>
public interface IChatInventoryState
{
    /// <summary>
    /// Most recent observation (count, timestamp) for the given chat-side
    /// <paramref name="displayName"/>, or <c>null</c> if no observation has
    /// been folded this session. Display-name keyed — the view layer is
    /// responsible for resolving to <c>InternalName</c>.
    /// </summary>
    bool TryGetLastObservation(string displayName, out int count, out DateTime timestamp);
}
