using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Mithril.Shared.Character;
using Mithril.Shared.Wpf;

namespace Celebrimbor.Domain;

public sealed class CelebrimborSettings : INotifyPropertyChanged, IVersionedState<CelebrimborSettings>
{
    /// <summary>
    /// Schema-version stamp (forward-compat hygiene per #208 — "any persisted JSON
    /// should carry a schema version"). Legacy files predate the field; per the
    /// codebase's IVersionedState convention <see cref="SchemaVersion"/> initialises
    /// to <see cref="Version"/>, and <see cref="Migrate"/> is an identity passthrough
    /// (no data loss). This is independent of #228 — the leveling plan is *not*
    /// stored here (it lives in a per-character <c>LevelingPlanState</c> store);
    /// CelebrimborSettings stays module-wide for the craft list / grid state.
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
