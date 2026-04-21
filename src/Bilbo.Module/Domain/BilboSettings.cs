using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Gorgon.Shared.Wpf;

namespace Bilbo.Domain;

public sealed class BilboSettings : INotifyPropertyChanged
{
    public DataGridState StorageGrid { get; set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(BilboSettings))]
public partial class BilboSettingsJsonContext : JsonSerializerContext;
