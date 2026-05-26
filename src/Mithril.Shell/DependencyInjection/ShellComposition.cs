using System.Collections.Frozen;
using Arda.Composition;
using Arda.Hosting;
using Arda.Wpf;
using Arda.World.Player;
using Mithril.Shared.Audio;
using Mithril.Shared.Character;
using Mithril.Shared.DependencyInjection;
using Mithril.Shared.Diagnostics.Performance;
using Mithril.Shared.Game;
using Mithril.Shared.Hotkeys;
using Mithril.Shared.Icons;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Mithril.Shared.Settings;
using Mithril.Shared.Wpf;
using Mithril.Shared.Wpf.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Mithril.Shell.DependencyInjection;

/// <summary>
/// Inputs the shell service graph needs that are computed by the bootstrap before
/// the host is built (settings already loaded, cache directories resolved). Carried
/// as a record so the <em>one</em> composition is shared verbatim between the real
/// launch path (<c>Program</c>) and the headless self-check (<c>--selfcheck</c> /
/// the DI-cycle guard test) — no second hand-maintained copy of the registration
/// chain to drift out of sync (#365, Layer 2).
/// </summary>
public sealed record ShellCompositionOptions(
    string PreferencesPath,
    ISettingsStore<ShellSettings> ShellStore,
    ShellSettings ShellSettings,
    GameConfig GameConfig,
    string LogDir,
    string PerfDir,
    string CharactersRootDir,
    string ReferenceCacheDir,
    string CommunityCalibrationCacheDir,
    string IconCacheDir,
    Action<string>? ModuleLog = null);

public static class ShellComposition
{
    /// <summary>
    /// Top-level composition entry point. Wires the full shell service graph.
    /// The Arda pipeline (L0–L3 + L4 composition) is the sole log-processing
    /// engine; the legacy world-sim merger was retired alongside
    /// <c>Mithril.GameState</c> and <c>Mithril.WorldSim.*</c>.
    /// </summary>
    public static IServiceCollection AddMithrilApp(
        this IServiceCollection services, ShellCompositionOptions o) =>
        services.AddMithrilShell(o);

    /// <summary>
    /// The complete shell service registration, in order. This is the single source
    /// of truth for the runtime DI graph; <c>Program</c> and the self-check both call
    /// it via <see cref="AddMithrilApp"/> so the guard validates exactly what ships.
    /// </summary>
    public static IServiceCollection AddMithrilShell(
        this IServiceCollection services, ShellCompositionOptions o)
    {
        services
            .AddMithrilSettings<UserPreferences>(o.PreferencesPath, UserPreferencesJsonContext.Default.UserPreferences)
            .AddSingleton<ISettingsStore<ShellSettings>>(o.ShellStore)
            .AddSingleton(o.ShellSettings)
            .AddSingleton<IActiveCharacterPersistence>(o.ShellSettings)
            .AddSingleton(o.GameConfig)
            .AddMithrilDiagnostics(o.LogDir)
            .AddMithrilPerfTrace(o.PerfDir, sp => () => sp.GetRequiredService<ShellSettings>().VerboseFrameEvents)
            .AddMithrilGameServices(sp => () => sp.GetRequiredService<ShellSettings>().MirrorRawLogLinesToDiagnostics)
            .AddMithrilLogActorPipeline(sp => () => sp.GetRequiredService<ShellSettings>().CaptureRawPlayerLogLines)
            // L1 driver — legacy pipeline retained for LogStreamAttentionSource
            // (subscription-health badge). Follow-on: retire once Arda exposes
            // equivalent health signaling.
            .AddMithrilLogStreamDriver()
            .AddMithrilPerCharacterStorage(o.CharactersRootDir)
            .AddMithrilReferenceData(o.ReferenceCacheDir)
            .AddMithrilCommunityCalibration(o.CommunityCalibrationCacheDir)
            .AddMithrilIcons(o.IconCacheDir)
            .AddMithrilAudio()
            .AddMithrilHotkeys()
            .AddMithrilDialogs()
            .AddMithrilModuleGates()
            // Register NoOp navigator BEFORE modules so module Register() calls
            // (which run inside AddMithrilModules) can override via AddSingleton.
            // Without this ordering, the NoOp would win and CanOpen would always
            // return false — chip cross-links would render disabled.
            .AddSingleton<IReferenceNavigator, NoOpReferenceNavigator>()
            .AddMithrilModules(o.ModuleLog)
            .AddMithrilAttention()
            .AddMithrilShellUpdates()
            .AddMithrilShellViews()
            .AddMithrilItemDetail()
            .AddMithrilIngredientSources()
            .AddMithrilShellCommands();

        // Arda pipeline (L0–L3 + L4 composition): the sole log-processing
        // engine. Uses the game root as log directory (Player.log + ChatLogs/).
        services
            .AddArda(new ArdaOptions(o.GameConfig.GameRoot))
            .AddPlayerWorld(
                itemPoolFactory: sp =>
                {
                    var refData = sp.GetRequiredService<IReferenceDataService>();
                    var keys = refData.ItemsByInternalName.Keys;
                    var identity = keys.ToFrozenDictionary(k => k, k => k, StringComparer.Ordinal);
                    return new Arda.Dispatch.InternPool(identity);
                },
                projectToGameHourFactory: _ => at => GameClock.Project(at).Hour,
                shiftsFactory: sp =>
                {
                    var catalog = sp.GetRequiredService<IShiftCatalog>();
                    return catalog.Shifts.Select(s => (s.Slug, s.StartHour)).ToList();
                });

        services.AddArdaComposition(
            o.CharactersRootDir,
            recipeKeyResolverFactory: sp =>
            {
                var refData = sp.GetRequiredService<IReferenceDataService>();
                return id =>
                {
                    var key = $"recipe_{id}";
                    return refData.Recipes.TryGetValue(key, out var recipe)
                        ? recipe.InternalName ?? key
                        : key;
                };
            });

        services.AddSingleton<InventoryProjection>();

        services.AddHostedService<ArdaDiagnosticBridge>();

        services.AddSingleton<SessionAgreementComposer>();
        services.AddSingleton<IAttentionSource>(sp => sp.GetRequiredService<SessionAgreementComposer>());
        services.AddHostedService(sp => sp.GetRequiredService<SessionAgreementComposer>());

        return services;
    }
}
