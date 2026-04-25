using System.Text.Json.Serialization;
using Mithril.Shared.Character;

namespace Gandalf.Domain;

/// <summary>
/// Per-timer runtime state, keyed by <see cref="GandalfTimerDef.Id"/>. All fields nullable:
/// a fresh record (<c>StartedAt == null</c>) means idle; <c>StartedAt</c> with no
/// <c>CompletedAt</c> is running; a stamped <c>CompletedAt</c> is done.
/// </summary>
public sealed class TimerProgress
{
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// Per-character timer progress. Stored at <c>characters/{slug}/gandalf.json</c> via
/// <see cref="PerCharacterView{T}"/>. Schema v2: pre-split v1 payloads carried full
/// <c>GandalfTimer</c> objects with intermixed definition + progress; the one-shot
/// <c>GandalfSplitMigration</c> lifts definitions up and leaves only this shape behind.
/// </summary>
public sealed class GandalfProgress : IVersionedState<GandalfProgress>
{
    public const int Version = 2;

    public static int CurrentVersion => Version;

    /// <summary>
    /// Identity migrate. v1 payloads are re-shaped into v2 by the one-shot
    /// <c>GandalfSplitMigration</c> hosted-service at startup, so anything that reaches
    /// this hook is either already v2 or an unrecognized blob we drop on the floor.
    /// </summary>
    public static GandalfProgress Migrate(GandalfProgress loaded)
    {
        if (loaded.SchemaVersion < Version)
        {
            // Legacy v1 readers shouldn't reach this path — the fanout has already rewritten
            // the file. If we're here, the fanout failed or missed this file; safest to drop
            // the payload so we don't leak v1 timer objects into the progress map.
            loaded.ByTimerId.Clear();
            loaded.SchemaVersion = Version;
        }
        return loaded;
    }

    public int SchemaVersion { get; set; } = Version;

    public Dictionary<string, TimerProgress> ByTimerId { get; set; } =
        new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(GandalfProgress))]
public partial class GandalfProgressJsonContext : JsonSerializerContext { }
