using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Gorgon.Shared.Reference;

/// <summary>
/// Shared per-module settings controlling how community-aggregated calibration data is merged
/// with the local user's observations and whether the app auto-refreshes it from GitHub.
/// Composed into each module's settings class (see SamwiseSettings.Calibration, ArwenSettings.Calibration).
/// </summary>
public sealed class CalibrationSettings : INotifyPropertyChanged
{
    private CalibrationSource _source = CalibrationSource.PreferLocal;
    private bool _autoRefreshCommunityData = true;

    public CalibrationSource Source { get => _source; set => Set(ref _source, value); }
    public bool AutoRefreshCommunityData { get => _autoRefreshCommunityData; set => Set(ref _autoRefreshCommunityData, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
