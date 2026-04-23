using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Gorgon.Shared.Reference;

namespace Arwen.Domain;

public sealed class ArwenSettings : INotifyPropertyChanged
{
    /// <summary>
    /// Persisted exact favor values keyed by character name, then NPC key.
    /// Survives app restarts so we don't lose Player.log data.
    /// </summary>
    public Dictionary<string, Dictionary<string, NpcFavorSnapshot>> FavorStates { get; set; } = new();

    private CalibrationSettings _calibration = new();
    /// <summary>Merge mode + auto-refresh toggle for the community-aggregated gift rates.</summary>
    public CalibrationSettings Calibration
    {
        get => _calibration;
        set
        {
            if (ReferenceEquals(_calibration, value)) return;
            _calibration.PropertyChanged -= OnCalibrationChanged;
            _calibration = value;
            _calibration.PropertyChanged += OnCalibrationChanged;
            OnChanged(nameof(Calibration));
        }
    }

    public ArwenSettings()
    {
        _calibration.PropertyChanged += OnCalibrationChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void OnCalibrationChanged(object? sender, PropertyChangedEventArgs e)
        => OnChanged(nameof(Calibration));

    /// <summary>Record or update exact favor for an NPC and trigger auto-save.</summary>
    public void SetExactFavor(string charName, string npcKey, double exactFavor, DateTimeOffset timestamp)
    {
        if (!FavorStates.TryGetValue(charName, out var charFavors))
        {
            charFavors = new(StringComparer.Ordinal);
            FavorStates[charName] = charFavors;
        }

        charFavors[npcKey] = new NpcFavorSnapshot { ExactFavor = exactFavor, Timestamp = timestamp };
        OnChanged(nameof(FavorStates));
    }

    /// <summary>Get persisted exact favor for a specific NPC, or null if unknown.</summary>
    public NpcFavorSnapshot? GetExactFavor(string charName, string npcKey)
    {
        if (FavorStates.TryGetValue(charName, out var charFavors) &&
            charFavors.TryGetValue(npcKey, out var snapshot))
            return snapshot;
        return null;
    }
}

public sealed class NpcFavorSnapshot
{
    public double ExactFavor { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ArwenSettings))]
[JsonSerializable(typeof(CalibrationSettings))]
public partial class ArwenJsonContext : JsonSerializerContext;
