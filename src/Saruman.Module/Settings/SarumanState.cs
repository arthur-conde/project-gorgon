using System.Text.Json.Serialization;
using Mithril.Shared.Character;

namespace Saruman.Settings;

/// <summary>
/// Per-character Saruman module state (#603 — post-codebook-split). The
/// codebook itself (discovery records + chat-spent state) is owned by
/// <see cref="Arda.Composition.IWordOfPowerComposer"/>; this state
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
    /// both have moved to <see cref="Arda.Composition.IWordOfPowerComposer"/> and are
    /// not re-imported here — discovery state rebuilds from log replay; chat
    /// spent state rebuilds from chat replay on first observation per
    /// (server, character). The override ledger starts empty.
    ///
    /// <para>Sets <see cref="ShowPreSplitMigrationHint"/> so the next time
    /// the user opens the Saruman tab they get a one-time banner explaining
    /// why their previously-marked-spent codes are missing and how to recover
    /// older offline burns via the right-click <c>Mark as spent</c> path.
    /// Users who already migrated under a Mithril build that lacked this flag
    /// won't see the hint — accepted trade-off, the only alternative was a
    /// retroactive heuristic (e.g. "discovered entries with no overrides")
    /// that overlaps with the legitimate fresh-install steady state.</para>
    /// </summary>
    public static SarumanState Migrate(SarumanState loaded)
    {
        loaded.SchemaVersion = Version;
        loaded.SpentOverrides ??= new HashSet<string>(StringComparer.Ordinal);
        loaded.ShowPreSplitMigrationHint = true;
        return loaded;
    }

    public int SchemaVersion { get; set; } = Version;

    /// <summary>
    /// Codes the user has manually marked Spent. Composes with
    /// <see cref="Arda.Composition.IWordOfPowerComposer"/>
    /// at the VM layer: <c>isSpent = view.IsSpent(code) || overrides.Contains(code)</c>.
    /// </summary>
    public HashSet<string> SpentOverrides { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// When <c>true</c>, the Saruman view surfaces a one-time banner explaining
    /// the pre-#603 spent-flag loss and the manual recovery path. Set by
    /// <see cref="Migrate"/> (schema 1→2 upgrade) and by
    /// <c>SarumanLegacyMigration</c> (pre-per-character flat-file migration);
    /// cleared when the user dismisses the banner. Defaults to <c>false</c> so
    /// fresh installs never see the notice.
    /// </summary>
    public bool ShowPreSplitMigrationHint { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SarumanState))]
public partial class SarumanJsonContext : JsonSerializerContext;
