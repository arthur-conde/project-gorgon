namespace Arda.Inventory;

/// <summary>
/// Abstraction over the character-keyed persistence layer so the ledger can
/// be tested without file I/O. Production uses <see cref="PerCharacterLedgerStateView"/>
/// which wraps <see cref="Mithril.Shared.Character.PerCharacterView{T}"/>.
/// </summary>
internal interface ILedgerStateView
{
    InventoryLedgerState? Current { get; }
    void Save();
    event EventHandler? CurrentChanged;
}
