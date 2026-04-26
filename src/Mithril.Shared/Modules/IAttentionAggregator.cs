using System.ComponentModel;

namespace Mithril.Shared.Modules;

/// <summary>
/// Shell-side aggregation of every registered <see cref="IAttentionSource"/>.
/// Exposes a per-module count (for sidebar binding) and a total + entry list
/// (for taskbar overlay and tray-menu binding). All change notifications are
/// marshalled through the dispatch callable supplied at construction.
/// </summary>
public interface IAttentionAggregator : INotifyPropertyChanged
{
    /// <summary>Sum across every registered source. Bind to taskbar / tray.</summary>
    int TotalCount { get; }

    /// <summary>True iff <see cref="TotalCount"/> &gt; 0. Convenience for visibility bindings.</summary>
    bool HasAttention { get; }

    /// <summary>Live snapshot. Iterates sources in registration order; safe to bind.</summary>
    IReadOnlyList<AttentionEntry> Entries { get; }

    /// <summary>Look up one module's count (for ModuleEntry chip binding). Returns 0 if no source.</summary>
    int CountFor(string moduleId);

    /// <summary>Per-module count change. Carries (moduleId, newCount).</summary>
    event EventHandler<AttentionChangedEventArgs>? AttentionChanged;
}

public sealed record AttentionEntry(string ModuleId, string DisplayLabel, int Count);

public sealed class AttentionChangedEventArgs(string moduleId, int count) : EventArgs
{
    public string ModuleId { get; } = moduleId;
    public int Count { get; } = count;
}
