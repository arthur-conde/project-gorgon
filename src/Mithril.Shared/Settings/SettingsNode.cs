using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Mithril.Shared.Settings;

/// <summary>
/// Base for INPC settings nodes that participate in a parent ↔ child
/// change-bubbling tree. <see cref="SettingsAutoSaver{T}"/> subscribes only
/// to the <em>root</em> instance's <see cref="INotifyPropertyChanged.PropertyChanged"/>
/// — without bubbling, a mutation on a nested INPC child fires on the
/// child only, the saver's dirty flag never flips, and the edit is lost
/// silently. (The bug that motivated this primitive: shift-alarm toggles
/// in <c>GandalfShiftSettings</c> never reaching <c>shifts.json</c>.)
///
/// Bubbling is opt-in per child: parents call <see cref="Bubble"/> when a
/// child is added (in property setters / dictionary <c>GetOrCreate</c> /
/// list <c>Add</c>) and <see cref="Unbubble"/> on removal. Nested children
/// loaded from JSON need to be re-wired post-deserialization — see
/// <see cref="IPostLoadInit"/>.
/// </summary>
public abstract class SettingsNode : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Set <paramref name="field"/> to <paramref name="value"/>; if the
    /// new value differs by <see cref="EqualityComparer{T}.Default"/>, fire
    /// <see cref="PropertyChanged"/> with the caller's name and return
    /// <c>true</c>.
    /// </summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    /// <summary>Fire <see cref="PropertyChanged"/> for an explicit property name.</summary>
    protected void RaisePropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// Subscribe to <paramref name="child"/>'s <see cref="INotifyPropertyChanged.PropertyChanged"/>
    /// so its mutations re-fire on this node. Pair every <see cref="Bubble"/>
    /// with an <see cref="Unbubble"/> on removal — subscribing the same
    /// child twice causes duplicate notifications.
    /// </summary>
    protected void Bubble(INotifyPropertyChanged? child)
    {
        if (child is null) return;
        child.PropertyChanged += OnChildChanged;
    }

    /// <summary>Reverse of <see cref="Bubble"/>. No-op if the child wasn't subscribed.</summary>
    protected void Unbubble(INotifyPropertyChanged? child)
    {
        if (child is null) return;
        child.PropertyChanged -= OnChildChanged;
    }

    private void OnChildChanged(object? sender, PropertyChangedEventArgs e) =>
        // Re-fire on this node. The saver only cares that *something*
        // changed, so passing the child's PropertyName upward is fine —
        // it doesn't conflict with any property on this parent.
        PropertyChanged?.Invoke(this, e);
}
