using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Silmarillion;

/// <summary>
/// Persisted preferences for the Silmarillion module. v1 only exposes
/// <see cref="UsedInChipCap"/>; the class exists primarily as scaffolding so future
/// settings (default tab, hide-empty-sections, …) can land as small follow-ups.
/// <see cref="SchemaVersion"/> is stamped from day one per the project's
/// versioned-JSON convention — cheap forward-compat without committing to the
/// <c>IVersionedState</c> migration path yet.
/// </summary>
public sealed class SilmarillionSettings : INotifyPropertyChanged
{
    /// <summary>Lowest valid cap. Zero collapses every "Used in" chip into the overflow pill.</summary>
    public const int MinUsedInChipCap = 0;

    /// <summary>Highest valid cap. Above ~100 the section becomes unbrowsable regardless of perf.</summary>
    public const int MaxUsedInChipCap = 100;

    /// <summary>Default cap: cheap case stays cheap, long tail collapses behind the overflow pill.</summary>
    public const int DefaultUsedInChipCap = 12;

    private int _schemaVersion = 1;
    private int _usedInChipCap = DefaultUsedInChipCap;

    /// <summary>
    /// Persisted schema version. Always written so a future <c>IVersionedState</c> migration
    /// switch can branch on the stored value instead of guessing.
    /// </summary>
    public int SchemaVersion
    {
        get => _schemaVersion;
        set => Set(ref _schemaVersion, value);
    }

    /// <summary>
    /// Maximum number of per-recipe chips rendered in the item-detail "Used in" section
    /// before collapsing the rest behind a <c>+N more →</c> overflow pill. Clamped to
    /// <see cref="MinUsedInChipCap"/>..<see cref="MaxUsedInChipCap"/>.
    /// </summary>
    public int UsedInChipCap
    {
        get => _usedInChipCap;
        set => Set(ref _usedInChipCap, Math.Clamp(value, MinUsedInChipCap, MaxUsedInChipCap));
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
[JsonSerializable(typeof(SilmarillionSettings))]
public partial class SilmarillionSettingsJsonContext : JsonSerializerContext { }
