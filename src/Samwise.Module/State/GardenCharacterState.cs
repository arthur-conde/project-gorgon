using System.Text.Json.Serialization;
using Mithril.Shared.Character;

namespace Samwise.State;

/// <summary>
/// Per-character Samwise state — persisted to <c>characters/{slug}/samwise.json</c>.
/// Each character's plot dict is in its own file.
///
/// <para><b>Schema v2 (#550 PR 3 archetype-B Samwise migration):</b> adds
/// <see cref="HighWaterSequence"/> — the largest L0 <c>Sequence</c> we've
/// already applied to this character's plot state. Samwise is the textbook
/// "persisted-state vs full-replay collision" case: <see cref="Plots"/> is
/// loaded from disk at gate-open, then the L1 driver replays the entire
/// session on the LocalPlayer pipe. Without a high-water, plant /
/// <c>UpdateDescription</c> / <c>StartInteraction</c> / <c>GardeningXp</c>
/// events re-apply on top of already-persisted plots, advancing stages
/// and burning slot caps. The high-water rides through to the L1
/// <c>SkipProcessedHighWater</c> filter (#550 capability F).</para>
///
/// <para>v1 → v2 migration is mechanical: the field defaults to 0, which
/// means "filter nothing" — the first session after upgrade behaves
/// exactly like pre-L1 (full replay re-applies idempotent-where-possible),
/// then subsequent sessions converge on the restart-stable behaviour. No
/// data loss; no user-visible change beyond the bug class shrinking.</para>
/// </summary>
public sealed class GardenCharacterState : IVersionedState<GardenCharacterState>
{
    public const int Version = 2;
    public static int CurrentVersion => Version;

    /// <summary>
    /// v1 → v2: stamp <see cref="HighWaterSequence"/> = 0 (the field's
    /// default), preserving the in-flight crops and slot-cap state already
    /// persisted under v1. Setting 0 means the L1 driver will not skip any
    /// envelope on the next session — equivalent to pre-L1 behaviour, which
    /// is the safe rollback shape. After one session under v2, the
    /// high-water advances naturally as events apply.
    /// </summary>
    public static GardenCharacterState Migrate(GardenCharacterState loaded)
    {
        // Plots survives verbatim across the bump; HighWaterSequence stays
        // at the property's default (0) since v1 didn't carry one.
        return loaded;
    }

    public int SchemaVersion { get; set; } = Version;

    public Dictionary<string, PersistedPlot> Plots { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Largest L0 <c>Sequence</c> already applied to <see cref="Plots"/> on
    /// this character. Fed back to the L1 driver's
    /// <c>SkipProcessedHighWater</c> filter so the
    /// <see cref="GardenStateMachine"/> doesn't re-process events that were
    /// already absorbed into the persisted state. 0 means "no filter" (fresh
    /// install / first-session-after-migration shape).
    /// </summary>
    public long HighWaterSequence { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(GardenCharacterState))]
public partial class GardenCharacterStateJsonContext : JsonSerializerContext { }
