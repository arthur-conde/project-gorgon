using System.IO;
using System.Text.Json;
using Gorgon.Shared.Character;
using Gorgon.Shared.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace Samwise.State;

/// <summary>
/// One-shot startup migration that splits the pre-per-character
/// <c>%LocalAppData%/Gorgon/Samwise/garden-state.json</c> blob (whose root is a
/// <c>Dictionary&lt;CharacterName, plotsDict&gt;</c>) into per-character
/// <c>characters/{slug}/samwise.json</c> files.
///
/// Runs during <see cref="IHostedService.StartAsync"/>, which the host awaits before any
/// module gate opens — so Samwise's ingestion (gated) can't race the split. Self-healing:
/// if a character in the legacy blob can't be resolved to a server (no export on disk
/// yet), their entry stays in the legacy file and next-startup retries.
/// </summary>
public sealed class GardenFanoutMigration : IHostedService
{
    private readonly string _legacyPath;
    private readonly PerCharacterStore<GardenCharacterState> _store;
    private readonly IActiveCharacterService _active;
    private readonly IDiagnosticsSink? _diag;

    public GardenFanoutMigration(
        string legacyDir,
        PerCharacterStore<GardenCharacterState> store,
        IActiveCharacterService active,
        IDiagnosticsSink? diag = null)
    {
        _legacyPath = Path.Combine(legacyDir, "garden-state.json");
        _store = store;
        _active = active;
        _diag = diag;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try { Run(); }
        catch (Exception ex) { _diag?.Warn("Samwise.Fanout", $"Fanout failed: {ex.Message}"); }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Run()
    {
        if (!File.Exists(_legacyPath)) return;

        GardenState? legacy;
        try
        {
            using var stream = File.OpenRead(_legacyPath);
            legacy = JsonSerializer.Deserialize(stream, GardenStateJsonContext.Default.GardenState);
        }
        catch (Exception ex)
        {
            _diag?.Warn("Samwise.Fanout", $"Legacy read failed: {ex.Message}");
            return;
        }
        if (legacy is null || legacy.PlotsByChar.Count == 0)
        {
            TryDeleteLegacy();
            return;
        }

        var unresolved = PerCharacterLegacyFanout.FanOut(
            names: legacy.PlotsByChar.Keys,
            store: _store,
            active: _active,
            extractFor: name => new GardenCharacterState
            {
                Plots = new Dictionary<string, PersistedPlot>(legacy.PlotsByChar[name], StringComparer.Ordinal),
            },
            diag: _diag);

        if (unresolved.Count == 0)
        {
            TryDeleteLegacy();
            _diag?.Info("Samwise.Fanout", "PlotsByChar fanout complete; legacy garden-state.json removed.");
        }
        else
        {
            // Rewrite legacy with only the unresolved characters so future startups don't
            // re-process those we've already moved.
            try
            {
                var pruned = new GardenState();
                foreach (var name in unresolved)
                    if (legacy.PlotsByChar.TryGetValue(name, out var plots))
                        pruned.PlotsByChar[name] = plots;

                using var stream = File.Create(_legacyPath);
                JsonSerializer.Serialize(stream, pruned, GardenStateJsonContext.Default.GardenState);
                _diag?.Info("Samwise.Fanout",
                    $"Legacy trimmed to {unresolved.Count} unresolved char(s): {string.Join(", ", unresolved)}");
            }
            catch (Exception ex)
            {
                _diag?.Warn("Samwise.Fanout", $"Legacy rewrite failed: {ex.Message}");
            }
        }
    }

    private void TryDeleteLegacy()
    {
        try
        {
            if (File.Exists(_legacyPath)) File.Delete(_legacyPath);
        }
        catch (Exception ex)
        {
            _diag?.Warn("Samwise.Fanout", $"Legacy delete failed: {ex.Message}");
        }
    }
}
