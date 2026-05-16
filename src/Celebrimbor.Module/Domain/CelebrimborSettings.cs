using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Mithril.Shared.Character;
using Mithril.Shared.Wpf;

namespace Celebrimbor.Domain;

public sealed class CelebrimborSettings : INotifyPropertyChanged, IVersionedState<CelebrimborSettings>
{
    /// <summary>
    /// First versioned schema. Legacy files predate versioning and deserialize with
    /// <see cref="SchemaVersion"/> = 0; the loader migrates them to <see cref="Version"/>.
    /// v1 only *adds* nullable plan state, so the migration is a pure identity passthrough —
    /// no existing CraftList / OnHandOverrides / grid state is touched (no data loss).
    /// </summary>
    public const int Version = 1;
    public static int CurrentVersion => Version;
    public static CelebrimborSettings Migrate(CelebrimborSettings loaded) => loaded;

    public int SchemaVersion { get; set; } = Version;

    private bool _knownRecipesOnly;
    private bool _enforceSkillLevel;
    private int _expansionDepth;
    private int _tooltipDelayMs = 200;

    public bool KnownRecipesOnly { get => _knownRecipesOnly; set => Set(ref _knownRecipesOnly, value); }
    public bool EnforceSkillLevel { get => _enforceSkillLevel; set => Set(ref _enforceSkillLevel, value); }

    /// <summary>0 = leave sub-recipe ingredients as-is; 1+ recurses that many levels.</summary>
    public int ExpansionDepth { get => _expansionDepth; set => Set(ref _expansionDepth, Math.Clamp(value, 0, 10)); }

    /// <summary>Milliseconds before a recipe tooltip shows on hover. Clamped 0-2000.</summary>
    public int TooltipDelayMs { get => _tooltipDelayMs; set => Set(ref _tooltipDelayMs, Math.Clamp(value, 0, 2000)); }

    public List<CraftListEntry> CraftList { get; set; } = [];
    public List<ManualOnHandOverride> OnHandOverrides { get; set; } = [];

    /// <summary>
    /// The leveling plan (#227) currently being walked, with its phase cursor and the
    /// sourcing snapshot it was planned under. <c>null</c> = no active plan. Survives
    /// sessions so the player picks up where they left off (#228).
    /// </summary>
    public PersistedPlan? ActivePlan { get; set; }

    public DataGridState RecipeGrid { get; set; } = new();
    public DataGridState IngredientGrid { get; set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Called by code that mutates the collection properties above
    /// (which can't raise INPC on their own).</summary>
    public void Touch([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(CelebrimborSettings))]
public partial class CelebrimborSettingsJsonContext : JsonSerializerContext;
