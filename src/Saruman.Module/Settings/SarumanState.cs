using System.Text.Json.Serialization;
using Mithril.Shared.Character;

namespace Saruman.Settings;

/// <summary>
/// Per-character Saruman module state (#603 — post-codebook-split). The
/// codebook itself (discovery records + chat-spent state) has moved to
/// <see cref="Mithril.GameState.WordsOfPower.IWordOfPowerView"/>; this state
/// holds only the module-internal user-override ledger.
///
/// <para><b>One-way Sticky Spent.</b> The user can manually mark a code Spent
/// for cases where the burn happened during an offline session (the
/// observability gap accepted by #603). There is no "clear" / "Known"
/// override — monotonic Spent makes Known-override mechanically meaningless
/// (a globally-Spent code can't be un-spent by the user toggling a flag).</para>
/// </summary>
public sealed class SarumanState : IVersionedState<SarumanState>
{
    public const int Version = 2;
    public static int CurrentVersion => Version;

    /// <summary>
    /// Migrate any pre-#603 saved state into the override-only shape.
    /// Pre-#603 instances carried <c>Codebook</c> + <c>DiscoveryHighWaterSequence</c>;
    /// both have moved to <see cref="Mithril.GameState.WordsOfPower"/> and are
    /// not re-imported here — discovery state rebuilds from log replay; chat
    /// spent state rebuilds from chat replay on first observation per
    /// (server, character). The override ledger starts empty.
    /// </summary>
    public static SarumanState Migrate(SarumanState loaded)
    {
        loaded.SchemaVersion = Version;
        loaded.SpentOverrides ??= new HashSet<string>(StringComparer.Ordinal);
        return loaded;
    }

    public int SchemaVersion { get; set; } = Version;

    /// <summary>
    /// Codes the user has manually marked Spent. Composes with
    /// <see cref="Mithril.GameState.WordsOfPower.IWordOfPowerView.IsSpent"/>
    /// at the VM layer: <c>isSpent = view.IsSpent(code) || overrides.Contains(code)</c>.
    /// </summary>
    public HashSet<string> SpentOverrides { get; set; } = new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SarumanState))]
public partial class SarumanJsonContext : JsonSerializerContext;
