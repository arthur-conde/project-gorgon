using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Mithril.Shared.Wpf;

namespace Elrond.Domain;

public sealed class ElrondSettings : INotifyPropertyChanged
{
    private string _lastSkill = "";
    public string LastSkill
    {
        get => _lastSkill;
        set => Set(ref _lastSkill, value);
    }

    private int? _lastGoalLevel;
    public int? LastGoalLevel
    {
        get => _lastGoalLevel;
        set => Set(ref _lastGoalLevel, value);
    }

    public DataGridState RecipeGrid { get; set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ElrondSettings))]
public partial class ElrondSettingsJsonContext : JsonSerializerContext { }
