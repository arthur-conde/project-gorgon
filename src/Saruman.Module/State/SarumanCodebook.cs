using System.Text.Json.Serialization;

namespace Saruman.State;

/// <summary>
/// Server-scoped Word-of-Power codebook. Unified store holding both discovery
/// records (from Player.log replay) and monotonic spent state (from chat
/// utterance scanning). Persists to a single file at
/// <c>%LocalAppData%/Mithril/Saruman/codebook.json</c>.
///
/// <para>WoPs are server-scoped (any character on the same server shares
/// the codebook), so each entry carries a <see cref="CodebookEntry.Server"/>
/// tag. The service filters by active server at query time.</para>
///
/// <para><b>Monotonic Spent.</b> Once <see cref="CodebookEntry.LastSpentAt"/>
/// is set, it is never cleared by the service. The user-override ledger in
/// <see cref="Settings.SarumanState.SpentOverrides"/> provides a separate
/// manual-mark path for offline burns.</para>
/// </summary>
public sealed class SarumanCodebook
{
    public const int CurrentVersion = 1;

    public int SchemaVersion { get; set; } = CurrentVersion;

    public List<CodebookEntry> Entries { get; set; } = [];

    public sealed class CodebookEntry
    {
        public required string Server { get; set; }
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
