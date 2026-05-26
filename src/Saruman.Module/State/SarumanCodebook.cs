using System.Text.Json.Serialization;
using Mithril.Shared.Character;

namespace Saruman.State;

/// <summary>
/// Per-character Word-of-Power codebook. Unified store holding both discovery
/// records (from Player.log replay) and monotonic spent state (from chat
/// utterance scanning). Persists to <c>saruman-codebook.json</c> via
/// <see cref="PerCharacterView{T}"/>.
///
/// <para><b>Monotonic Spent.</b> Once <see cref="CodebookEntry.LastSpentAt"/>
/// is set, it is never cleared by the service. The user-override ledger in
/// <see cref="Settings.SarumanState.SpentOverrides"/> provides a separate
/// manual-mark path for offline burns.</para>
/// </summary>
public sealed class SarumanCodebook : IVersionedState<SarumanCodebook>
{
    public const int Version = 1;
    public static int CurrentVersion => Version;
    public static SarumanCodebook Migrate(SarumanCodebook loaded) => loaded;

    public int SchemaVersion { get; set; } = Version;

    public Dictionary<string, CodebookEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public sealed class CodebookEntry
    {
        public required string Code { get; set; }
        public required string Effect { get; set; }
        public string? Description { get; set; }
        public DateTimeOffset DiscoveredAt { get; set; }
        public DateTimeOffset? LastSpentAt { get; set; }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SarumanCodebook))]
public partial class SarumanCodebookJsonContext : JsonSerializerContext;
