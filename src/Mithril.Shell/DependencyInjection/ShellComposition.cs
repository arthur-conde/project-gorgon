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
    /// The complete shell service registration, in order. This is the single source
    /// of truth for the runtime DI graph; <c>Program</c> and the self-check both call
    /// it so the guard validates exactly what ships.
    /// </summary>
    public static IServiceCollection AddMithrilShell(
        this IServiceCollection services, ShellCompositionOptions o) =>
        services
            .AddMithrilSettings<UserPreferences>(o.PreferencesPath, UserPreferencesJsonContext.Default.UserPreferences)
            .AddSingleton<ISettingsStore<ShellSettings>>(o.ShellStore)
            .AddSingleton(o.ShellSettings)
            .AddSingleton<IActiveCharacterPersistence>(o.ShellSettings)
            .AddSingleton(o.GameConfig)
            .AddMithrilDiagnostics(o.LogDir)
            .AddMithrilPerfTrace(o.PerfDir, sp => () => sp.GetRequiredService<ShellSettings>().VerboseFrameEvents)
            .AddMithrilGameServices()
            .AddMithrilLogActorRouter(sp => () => sp.GetRequiredService<ShellSettings>().CaptureRawPlayerLogLines)
            // L1 driver — consumed by archetype-A GameState producers (#550 PR 2)
            // and the archetype-B consumer fleet (#550 PR 3..N). Registered
            // between the L0.5 router (which it consumes via the typed pipes)
            // and AddMithrilGameState (whose producers depend on ILogStreamDriver).
            .AddMithrilLogStreamDriver()
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
}
