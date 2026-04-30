using Gandalf.Domain;

namespace Gandalf.Services;

/// <summary>
/// <see cref="ITimerSource"/> adapter for the user-curated timer feed. Wraps
/// <see cref="TimerDefinitionsService"/> (catalog) and <see cref="TimerProgressService"/>
/// (progress) and bridges <c>TimerExpired</c> into the cross-source <c>TimerReady</c>
/// surface. User-tab mutation commands route through the underlying services as
/// before — Quest/Loot sources will own their own analogues when those phases land.
/// </summary>
public sealed class UserTimerSource : ITimerSource, IDisposable
{
    public const string Id = "gandalf.user";

    private readonly TimerDefinitionsService _defs;
    private readonly TimerProgressService _progress;
    private IReadOnlyList<TimerCatalogEntry> _catalog;
    private IReadOnlyDictionary<string, TimerProgressEntry> _progressMap;

    public UserTimerSource(TimerDefinitionsService defs, TimerProgressService progress)
    {
        _defs = defs;
        _progress = progress;
        _catalog = ProjectCatalog(defs);
        _progressMap = ProjectProgress(defs, progress);

        _defs.DefinitionsChanged += OnDefinitionsChanged;
        _progress.ProgressChanged += OnProgressChanged;
        _progress.TimerExpired += OnTimerExpired;
    }

    public string SourceId => Id;
    public IReadOnlyList<TimerCatalogEntry> Catalog => _catalog;
    public IReadOnlyDictionary<string, TimerProgressEntry> Progress => _progressMap;

    public event EventHandler? CatalogChanged;
    public event EventHandler? ProgressChanged;
    public event EventHandler<TimerReadyEventArgs>? TimerReady;

    private void OnDefinitionsChanged(object? sender, EventArgs e)
    {
        _catalog = ProjectCatalog(_defs);
        // A definition swap can orphan progress entries; reproject so consumers see a
        // consistent (catalog, progress) snapshot.
        _progressMap = ProjectProgress(_defs, _progress);
        CatalogChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnProgressChanged(object? sender, EventArgs e)
    {
        _progressMap = ProjectProgress(_defs, _progress);
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTimerExpired(object? sender, TimerExpiredEventArgs e)
    {
        TimerReady?.Invoke(this, new TimerReadyEventArgs
        {
            SourceId = Id,
            Key = e.Def.Id,
            DisplayName = e.Def.Name,
            ReadyAt = e.Progress.CompletedAt ?? DateTimeOffset.UtcNow,
            SourceMetadata = e.Def,
        });
    }

    private static IReadOnlyList<TimerCatalogEntry> ProjectCatalog(TimerDefinitionsService defs) =>
        defs.Definitions
            .Select(d => new TimerCatalogEntry(
                Key: d.Id,
                DisplayName: d.Name,
                Region: d.GroupKey,
                Duration: d.Duration,
                SourceMetadata: d))
            .ToArray();

    private static IReadOnlyDictionary<string, TimerProgressEntry> ProjectProgress(
        TimerDefinitionsService defs,
        TimerProgressService progress)
    {
        var map = new Dictionary<string, TimerProgressEntry>(StringComparer.Ordinal);
        foreach (var def in defs.Definitions)
        {
            var p = progress.GetProgress(def.Id);
            if (p?.StartedAt is null) continue;
            map[def.Id] = new TimerProgressEntry(def.Id, p.StartedAt.Value, DismissedAt: null);
        }
        return map;
    }

    public void Dispose()
    {
        _defs.DefinitionsChanged -= OnDefinitionsChanged;
        _progress.ProgressChanged -= OnProgressChanged;
        _progress.TimerExpired -= OnTimerExpired;
    }
}
