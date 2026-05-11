using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Samwise.Alarms;

public enum AlarmCollisionBehavior
{
    /// <summary>Concurrent alarms layer freely on this channel.</summary>
    Mix,
    /// <summary>A new alarm stops any currently-playing one on this channel before playing.</summary>
    Replace,
    /// <summary>A new alarm is silenced if any other alarm is currently playing on this channel.</summary>
    Suppress,
}

/// <summary>
/// A user-named group of stage alarms with a shared collision policy.
/// Stages route to channels via StageAlarmRule.ChannelId.
/// </summary>
public sealed class AlarmChannel : INotifyPropertyChanged
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    private string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    private AlarmCollisionBehavior _collision = AlarmCollisionBehavior.Mix;
    public AlarmCollisionBehavior Collision { get => _collision; set => Set(ref _collision, value); }

    /// <summary>
    /// Human-readable list of the stages currently routed to this channel.
    /// Recomputed and assigned externally by AlarmSettings whenever
    /// rule channel assignments change; AlarmChannel just exposes/notifies.
    /// </summary>
    private string _membershipSummary = "";
    public string MembershipSummary { get => _membershipSummary; set => Set(ref _membershipSummary, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T f, T v, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(f, v)) return;
        f = v;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
