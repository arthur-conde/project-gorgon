using System.Text.Json.Serialization;
using Mithril.Shared.Character;

namespace Gandalf.Domain;

/// <summary>
/// Per-row derived-source progress: log-anchored <c>StartedAt</c> (the moment of the
/// quest completion or chest loot, not when Mithril observed it), plus optional
/// <c>DismissedAt</c> for the "row hidden until next observation" model. No
/// <c>CompletedAt</c> — derived rows compute readiness from <c>StartedAt + Duration</c>
/// so the timestamp is tick-order-independent.
/// </summary>
public sealed class DerivedTimerProgress
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? DismissedAt { get; set; }
}

/// <summary>
/// Per-character derived-source progress. One file per character —
/// <c>characters/{slug}/gandalf-derived.json</c> — holds entries from every derived
/// source (Quest, Loot, future ...), namespaced by <see cref="ITimerSource.SourceId"/>
/// to keep cross-source key collisions impossible. User timers stay in
/// <see cref="GandalfProgress"/>; their lifecycle (manual Start, no DismissedAt,
/// CompletedAt for fire-once alarm bookkeeping) is materially different.
/// </summary>
public sealed class DerivedProgress : IVersionedState<DerivedProgress>
{
    public const int Version = 1;

    public static int CurrentVersion => Version;

    public static DerivedProgress Migrate(DerivedProgress loaded)
    {
        if (loaded.SchemaVersion < Version)
        {
            loaded.BySource.Clear();
            loaded.SchemaVersion = Version;
        }
        return loaded;
    }

    public int SchemaVersion { get; set; } = Version;

    /// <summary>Outer dict keyed by <see cref="ITimerSource.SourceId"/>; inner by row key.</summary>
    public Dictionary<string, Dictionary<string, DerivedTimerProgress>> BySource { get; set; } =
        new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(DerivedProgress))]
public partial class DerivedProgressJsonContext : JsonSerializerContext { }
