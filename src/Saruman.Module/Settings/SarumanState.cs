using System.Text.Json.Serialization;
using Mithril.Shared.Character;
using Mithril.Shared.Logging;
using Saruman.Domain;

namespace Saruman.Settings;

public sealed class SarumanState : IVersionedState<SarumanState>
{
    public const int Version = 1;
    public static int CurrentVersion => Version;
    public static SarumanState Migrate(SarumanState loaded) => loaded;

    public int SchemaVersion { get; set; } = Version;

    /// <summary>Known words keyed by uppercase code.</summary>
    public Dictionary<string, KnownWord> Codebook { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Highest <c>LocalPlayerLogLine.Sequence</c> already applied to
    /// <see cref="Codebook"/> via <c>RecordDiscovery</c>. Persisted so that on
    /// a Mithril restart the L1 driver can replay the LocalPlayer pipe with
    /// <c>SkipProcessedHighWater = DiscoveryHighWaterSequence</c> and the
    /// monotonic <see cref="KnownWord.DiscoveryCount"/> bumps don't re-inflate.
    /// Null until the first discovery has been recorded — the driver treats a
    /// null/missing high-water as "no filter" (capability F of #550).
    ///
    /// <para>This is per-character because <see cref="Codebook"/> is
    /// per-character (the canonical owner of the active-character split is
    /// <see cref="PerCharacterView{T}"/>). A character switch swaps both the
    /// codebook and its high-water atomically; no cross-character leakage.</para>
    ///
    /// <para>Defence-in-depth alongside <c>ReplayMode.SinceSubscribe</c>:
    /// SinceSubscribe drops every replay envelope structurally (and is
    /// today's primary defense), but persisting the high-water keeps the
    /// filter useful if SinceSubscribe's behaviour later changes to a
    /// replay-window variant (see <see cref="ReplayMode.SinceSubscribe"/>
    /// docs) without a code change here.</para>
    ///
    /// <para>Field is added without a SchemaVersion bump because the default
    /// (<c>null</c>) reads correctly out of legacy <c>SchemaVersion = 1</c>
    /// files — System.Text.Json source-generated deserialisation leaves
    /// missing fields at their default value. <see cref="Migrate"/> stays an
    /// identity passthrough.</para>
    /// </summary>
    public long? DiscoveryHighWaterSequence { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SarumanState))]
public partial class SarumanJsonContext : JsonSerializerContext;
