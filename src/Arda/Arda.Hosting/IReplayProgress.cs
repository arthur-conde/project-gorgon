using System.ComponentModel;

namespace Arda.Hosting;

/// <summary>
/// Reports replay progress for display in a splash screen. Bindable from WPF
/// via <see cref="INotifyPropertyChanged"/>.
/// <para>
/// Progress values range from 0.0 (nothing replayed) to 1.0 (caught up).
/// <see cref="ReplayComplete"/> resolves when all source families have
/// transitioned from replay to live tailing.
/// </para>
/// </summary>
public interface IReplayProgress : INotifyPropertyChanged
{
    /// <summary>Progress for the Player.log family (0.0 to 1.0).</summary>
    double PlayerProgress { get; }

    /// <summary>Progress for the Chat log family (0.0 to 1.0).</summary>
    double ChatProgress { get; }

    /// <summary>
    /// Completes when all sources have caught up and <c>IsReplay</c> has
    /// flipped to <c>false</c> for every active driver. Modules gate their
    /// activation on this task.
    /// </summary>
    Task ReplayComplete { get; }
}
