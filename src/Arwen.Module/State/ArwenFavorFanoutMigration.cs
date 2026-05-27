using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json;
using Arwen.Domain;
using Mithril.Shared.Character;
using Mithril.Shared.Settings;
using Microsoft.Extensions.Hosting;

namespace Arwen.State;

/// <summary>
/// One-shot startup migration that splits the pre-per-character <c>FavorStates</c> dictionary
/// (nested inside <c>%LocalAppData%/Mithril/Arwen/settings.json</c>) into per-character
/// <c>arwen.json</c> files, then rewrites the settings file without that field.
///
/// Runs during <see cref="IHostedService.StartAsync"/>, which the host awaits before any
/// module gate opens — so Arwen's ingestion (gated) can't race the split.
///
/// Self-healing: if a character in the legacy blob can't be resolved to a server (no
/// export on disk yet), their entry stays in the legacy file and next-startup retries.
/// The settings file is rewritten (losing FavorStates) only when every character resolved.
/// </summary>
public sealed class ArwenFavorFanoutMigration : IHostedService
{
    private readonly string _legacyPath;
    private readonly PerCharacterStore<ArwenFavorState> _store;
    private readonly PerCharacterView<ArwenFavorState> _view;
    private readonly IActiveCharacterService _active;
    private readonly ISettingsStore<ArwenSettings> _settingsStore;
    private readonly ArwenSettings _settings;
    private readonly ILogger? _logger;

    public ArwenFavorFanoutMigration(
        string legacyDir,
        PerCharacterStore<ArwenFavorState> store,
        PerCharacterView<ArwenFavorState> view,
        IActiveCharacterService active,
        ISettingsStore<ArwenSettings> settingsStore,
        ArwenSettings settings,
        ILogger? logger = null)
    {
        _legacyPath = Path.Combine(legacyDir, "settings.json");
        _store = store;
        _view = view;
        _active = active;
        _settingsStore = settingsStore;
        _settings = settings;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try { Run(); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Fanout failed"); }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Run()
    {
        if (!File.Exists(_legacyPath)) return;

        ArwenLegacySettings? legacy;
        try
        {
            using var stream = File.OpenRead(_legacyPath);
            legacy = JsonSerializer.Deserialize(stream, ArwenLegacyJsonContext.Default.ArwenLegacySettings);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Legacy read failed");
            return;
        }
        if (legacy is null || legacy.FavorStates.Count == 0) return;

        var unresolved = PerCharacterLegacyFanout.FanOut(
            names: legacy.FavorStates.Keys,
            store: _store,
            active: _active,
            extractFor: name => new ArwenFavorState
            {
                Favor = new Dictionary<string, NpcFavorSnapshot>(legacy.FavorStates[name], StringComparer.Ordinal),
            },
            view: _view,
            logger: _logger);

        if (unresolved.Count == 0)
        {
            // Drop FavorStates from the settings file — write back the live in-memory settings
            // (which are already FavorStates-free in the new shape).
            try
            {
                _settingsStore.Save(_settings);
                _logger?.LogInformation("FavorStates fanout complete; settings file trimmed.");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Settings rewrite failed");
            }
        }
        else
        {
            _logger?.LogInformation($"FavorStates retained for next-startup retry. Unresolved: {string.Join(", ", unresolved)}");
        }
    }
}
