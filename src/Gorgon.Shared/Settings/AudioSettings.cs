using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Gorgon.Shared.Settings;

public sealed class AudioSettings : INotifyPropertyChanged
{
    private bool _concurrentAlarms;

    public bool ConcurrentAlarms
    {
        get => _concurrentAlarms;
        set => Set(ref _concurrentAlarms, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
