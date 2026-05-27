using Microsoft.Extensions.Logging;
using Gandalf.Domain;
using Microsoft.Extensions.Hosting;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;

namespace Gandalf.Services;

/// <summary>
/// Pulls defeat-cooldown durations from the community calibration service and
/// pushes them into <see cref="LootSource"/> via
/// <see cref="LootSource.OverlayDefeatCalibration"/>. Re-applies on
/// <see cref="ICommunityCalibrationService.FileUpdated"/> for the
/// <c>"gandalf"</c> key (cache load on startup + background refresh).
///
/// Lifecycle is a hosted service so the initial overlay applies as soon as
/// the host starts, before any wisdom-credit events arrive.
/// </summary>
public sealed class DefeatCalibrationBridge : IHostedService, IDisposable
{
    private readonly ICommunityCalibrationService _community;
    private readonly LootSource _lootSource;
    private readonly ILogger? _logger;

    public DefeatCalibrationBridge(
        ICommunityCalibrationService community,
        LootSource lootSource,
        ILogger? logger = null)
    {
        _community = community;
        _lootSource = lootSource;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _community.FileUpdated += OnFileUpdated;
        // Apply whatever's already in memory (cache load happens in the
        // service ctor before this hosted service starts).
        ApplyOverlay();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _community.FileUpdated -= OnFileUpdated;
        return Task.CompletedTask;
    }

    public void Dispose() => _community.FileUpdated -= OnFileUpdated;

    private void OnFileUpdated(object? sender, string key)
    {
        if (!string.Equals(key, "gandalf", StringComparison.Ordinal)) return;
        ApplyOverlay();
    }

    private void ApplyOverlay()
    {
        var payload = _community.GandalfDefeats;
        if (payload is null)
        {
            // No calibration yet — leave the overlay empty so auto-discovered
            // bosses fall through to the placeholder duration.
            _lootSource.OverlayDefeatCalibration([]);
            return;
        }

        var entries = payload.Defeats
            .Where(kvp => kvp.Value.DurationSeconds > 0)
            .Select(kvp => new DefeatCatalogEntry(
                DisplayName: kvp.Key,
                RewardCooldown: TimeSpan.FromSeconds(kvp.Value.DurationSeconds),
                Area: kvp.Value.Area))
            .ToArray();

        _lootSource.OverlayDefeatCalibration(entries);
        _logger?.LogDiagnosticInfo("Gandalf.Loot", $"Applied defeat calibration ({entries.Length} entries).");
    }
}
