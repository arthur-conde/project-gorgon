using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Pippin.Domain;

public sealed class PippinSettings : INotifyPropertyChanged
{
    private string _viewMode = "Grid";
    public string ViewMode
    {
        get => _viewMode;
        set => Set(ref _viewMode, value);
    }

    private bool _hideLocked;
    public bool HideLocked
    {
        get => _hideLocked;
        set => Set(ref _hideLocked, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(PippinSettings))]
public partial class PippinSettingsJsonContext : JsonSerializerContext { }
