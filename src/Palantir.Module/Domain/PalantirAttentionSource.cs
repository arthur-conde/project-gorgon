using Mithril.Shared.Modules;

namespace Palantir.Domain;

/// <summary>
/// Developer-only attention source driven by the Notification Tester tab.
/// The shell aggregator picks this up alongside real sources, so Bump/Reset
/// drives the same sidebar pip + tray badge surface that production modules use.
/// </summary>
public sealed class PalantirAttentionSource : IAttentionSource
{
    private int _count;

    public string ModuleId => "palantir";
    public string DisplayLabel => "Palantir — notification tester";

    public int Count => _count;

    public event EventHandler? Changed;

    public void SetCount(int count)
    {
        var clamped = Math.Max(0, count);
        if (_count == clamped) return;
        _count = clamped;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Bump() => SetCount(_count + 1);
    public void Decrement() => SetCount(_count - 1);
    public void Clear() => SetCount(0);
}
