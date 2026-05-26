using Mithril.Shared.Character;

namespace Arda.Inventory;

/// <summary>
/// Adapts <see cref="PerCharacterView{T}"/> to <see cref="ILedgerStateView"/>.
/// </summary>
internal sealed class PerCharacterLedgerStateView(PerCharacterView<InventoryLedgerState> inner) : ILedgerStateView
{
    public InventoryLedgerState? Current => inner.Current;
    public void Save() => inner.Save();

    public event EventHandler? CurrentChanged
    {
        add => inner.CurrentChanged += value;
        remove => inner.CurrentChanged -= value;
    }
}
