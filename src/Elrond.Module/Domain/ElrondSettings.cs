using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

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

    private string _sortKey = "EffectiveXp";
    public string SortKey
    {
        get => _sortKey;
        set => Set(ref _sortKey, value);
    }

    private bool _sortDescending = true;
    public bool SortDescending
    {
        get => _sortDescending;
        set => Set(ref _sortDescending, value);
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
[JsonSerializable(typeof(ElrondSettings))]
public partial class ElrondSettingsJsonContext : JsonSerializerContext { }
