using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gandalf.Domain;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;
using Microsoft.Extensions.Hosting;

namespace Gandalf.Services;

/// <summary>
/// One-shot v2→v3 startup migration for the global Gandalf <c>definitions.json</c>.
/// v2 timers carried a free-form <c>region</c> + <c>map</c> pair; v3 collapses those
/// into a single <c>area</c> string with an optional canonical <c>areaKey</c> resolved
/// against <c>areas.json</c>.
///
/// Operates at the JSON level (<see cref="JsonNode"/>) instead of going through the typed
/// <see cref="GandalfDefinitions"/> model — that model has already dropped Region/Map, so
/// a typed round-trip would silently lose them. Mirrors <see cref="GandalfSplitMigration"/>'s
/// pre-startup file-rewrite pattern. Idempotent: schemaVersion ≥ <see cref="GandalfDefinitions.Version"/>
/// or missing-file early-returns.
/// </summary>
public sealed class GandalfAreaFlattenMigration : IHostedService
{
    private readonly string _defsPath;
    private readonly IReferenceDataService _refData;
    private readonly ILogger? _logger;

    public GandalfAreaFlattenMigration(
        string defsPath,
        IReferenceDataService refData,
        ILogger? logger = null)
    {
        _defsPath = defsPath;
        _refData = refData;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try { Run(); }
        catch (Exception ex) { _logger?.LogDiagnosticWarn("Gandalf.AreaFlatten", $"Failed: {ex.Message}"); }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal void Run()
    {
        if (!File.Exists(_defsPath)) return;

        JsonNode? root;
        using (var stream = File.OpenRead(_defsPath))
        {
            root = JsonNode.Parse(stream);
        }
        if (root is not JsonObject obj) return;

        var schemaVersion = obj["schemaVersion"]?.GetValue<int>() ?? 0;
        if (schemaVersion >= GandalfDefinitions.Version) return;

        if (obj["timers"] is not JsonArray timers) return;

        var lookup = GandalfAreaResolver.BuildLookup(_refData);
        int migrated = 0;
        foreach (var node in timers)
        {
            if (node is not JsonObject t) continue;
            var region = (string?)t["region"] ?? "";
            var map = (string?)t["map"] ?? "";
            var (area, areaKey) = GandalfAreaResolver.FlattenLegacy(region, map, lookup);

            t.Remove("region");
            t.Remove("map");
            t["area"] = area;
            // null is preserved verbatim by JsonObject — matches AreaKey's nullable JSON shape.
            t["areaKey"] = areaKey;
            migrated++;
        }

        obj["schemaVersion"] = GandalfDefinitions.Version;

        var serialized = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_defsPath, serialized);
        _logger?.LogDiagnosticInfo("Gandalf.AreaFlatten",
            $"Migrated {migrated} timer definition(s) from v{schemaVersion} to v{GandalfDefinitions.Version}.");
    }
}
