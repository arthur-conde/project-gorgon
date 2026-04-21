using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Gorgon.Shared.Icons;

public sealed class IconSettings : INotifyPropertyChanged
{
    public const string DefaultUrlPattern = "https://cdn.projectgorgon.com/{version}/icons/icon_{iconId}.png";

    private bool _enabled = true;
    private string _urlPattern = DefaultUrlPattern;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string UrlPattern { get => _urlPattern; set => Set(ref _urlPattern, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(IconSettings))]
public partial class IconSettingsJsonContext : JsonSerializerContext { }
