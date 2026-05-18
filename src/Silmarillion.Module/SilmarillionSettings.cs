using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Silmarillion;

/// <summary>
/// Persisted preferences for the Silmarillion module. Exposes per-tab chip caps that
/// trade off detail-pane chip density against the overflow pill's deep-link route.
/// <see cref="SchemaVersion"/> is stamped from day one per the project's versioned-JSON
/// convention — cheap forward-compat without committing to the <c>IVersionedState</c>
/// migration path yet.
/// </summary>
public sealed class SilmarillionSettings : INotifyPropertyChanged
{
    /// <summary>Lowest valid cap. Zero collapses every chip into the overflow pill.</summary>
    public const int MinUsedInChipCap = 0;

    /// <summary>Highest valid cap. Above ~100 the section becomes unbrowsable regardless of perf.</summary>
    public const int MaxUsedInChipCap = 100;

    /// <summary>Default cap: cheap case stays cheap, long tail collapses behind the overflow pill.</summary>
    public const int DefaultUsedInChipCap = 12;

    /// <summary>Default cap for the Effects-tab "Required by abilities" chip cluster.</summary>
    public const int DefaultRequiredByAbilitiesChipCap = 12;

    /// <summary>
    /// Default cap for the NPC-detail chip enumerations ("Teaches recipes", "Teaches
    /// abilities", "Sells items", "Quests"). An NPC like a major crafting trainer teaches
    /// 60+ recipes and sells 30+ items — every chip carries an icon that decodes on the UI
    /// thread, so an uncapped detail stalls the pane on selection.
    /// </summary>
    public const int DefaultNpcChipCap = 12;

    private int _schemaVersion = 1;
    private int _usedInChipCap = DefaultUsedInChipCap;
    private int _requiredByAbilitiesChipCap = DefaultRequiredByAbilitiesChipCap;
    private int _npcChipCap = DefaultNpcChipCap;

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

    /// <summary>
    /// Maximum number of per-ability chips rendered in the effect-detail
    /// "Required by abilities" section before the rest are reachable via the always-visible
    /// "View all N →" affordance. That affordance opens the shared provenance popup fed
    /// <see cref="Mithril.Shared.Reference.IReferenceDataService.AbilitiesByEffectKeyword"/>
    /// directly (#318) — no synthetic-kind deep link, no query re-derivation. Clamped to
    /// <see cref="MinUsedInChipCap"/>..<see cref="MaxUsedInChipCap"/>.
    /// </summary>
    public int RequiredByAbilitiesChipCap
    {
        get => _requiredByAbilitiesChipCap;
        set => Set(ref _requiredByAbilitiesChipCap, Math.Clamp(value, MinUsedInChipCap, MaxUsedInChipCap));
    }

    /// <summary>
    /// Maximum number of chips rendered in each NPC-detail enumeration ("Teaches recipes",
    /// "Teaches abilities", "Sells items", "Quests") before the remainder is reachable via
    /// the always-visible "View all N →" affordance that opens the shared provenance popup.
    /// A separate knob from <see cref="UsedInChipCap"/> so NPC density tunes independently of
    /// the item-detail "Used in" cluster. Clamped to
    /// <see cref="MinUsedInChipCap"/>..<see cref="MaxUsedInChipCap"/>.
    /// </summary>
    public int NpcChipCap
    {
        get => _npcChipCap;
        set => Set(ref _npcChipCap, Math.Clamp(value, MinUsedInChipCap, MaxUsedInChipCap));
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
