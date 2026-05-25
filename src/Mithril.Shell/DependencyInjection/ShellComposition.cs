using Arda.Hosting;
using Arda.World.Player;
using Mithril.Shared.Audio;
using Mithril.Shared.Character;
using Mithril.GameState.DependencyInjection;
using Mithril.Shared.DependencyInjection;
using Mithril.Shared.Diagnostics.Performance;
using Mithril.Shared.Game;
using Mithril.Shared.Hotkeys;
using Mithril.Shared.Icons;
using Mithril.Shared.Reference;
using Mithril.Shared.Settings;
using Mithril.Shared.Wpf;
using Mithril.Shared.Wpf.DependencyInjection;
using Mithril.WorldSim.Chat.DependencyInjection;
using Mithril.WorldSim.Player.DependencyInjection;
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
    /// Top-level composition entry point. Wires the full shell service graph
    /// AND appends the trailing <see cref="WorldMergerStartHostedService"/>
    /// (#696 Call 2) so each world's merger drain starts strictly after every
    /// other hosted service has finished its registration work. The "registered
    /// LAST" invariant is enforced structurally — call sites use this method
    /// rather than <see cref="AddMithrilShell"/> so they cannot forget the
    /// trailing merger start, and a future contributor adding another shell
    /// registration edits <see cref="AddMithrilShell"/> while the trailing
    /// invariant is preserved automatically here.
    /// </summary>
    public static IServiceCollection AddMithrilApp(
        this IServiceCollection services, ShellCompositionOptions o) =>
        services
            .AddMithrilShell(o)
            .AddWorldMergerStart();

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
            // L1 driver — consumed by archetype-A GameState producers (#550 PR 2)
            // and the archetype-B consumer fleet (#550 PR 3..N). Registered
            // between the L0.5 classifier+splitter (which it consumes via
            // the typed pipes + the unified pipe — #556) and
            // AddMithrilGameState (whose producers depend on ILogStreamDriver).
            .AddMithrilLogStreamDriver()
            // PlayerWorld + ChatWorld register the world singletons and the
            // L1 / chat-tail producers. Under #696 (Call 2) neither extension
            // registers a hosted service — the merger drain starts trailing,
            // from the WorldMergerStartHostedService appended by AddMithrilApp.
            // Registration order between AddPlayerWorld / AddChatWorld and
            // AddMithrilGameState is therefore irrelevant for hosted-service
            // ordering; resolution-order is what matters, and DI resolution is
            // order-independent.
            .AddPlayerWorld()
            .AddChatWorld()
            .AddMithrilGameState()
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

        // Arda pipeline (L0–L3): runs side-by-side with the legacy world sim.
        // Uses the game root as log directory (Player.log + ChatLogs/).
        services
            .AddArda(new ArdaOptions(o.GameConfig.GameRoot))
            .AddPlayerWorld();

        services.AddHostedService<ArdaDiagnosticBridge>();

        return services;
    }
}
