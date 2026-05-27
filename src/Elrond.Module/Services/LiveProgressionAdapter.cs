using Arda.Composition;
using Mithril.GameReports;
using Mithril.Shared.Character;

namespace Elrond.Services;

/// <summary>
/// Merges live <see cref="IPlayerProgressionState"/> (Arda L4) with character-export
/// snapshots from <see cref="IGameReportsService"/>. Live values win on overlap;
/// export fills gaps for skills/recipes never mentioned in the current session.
/// </summary>
public sealed class LiveProgressionAdapter : IDisposable
{
    private readonly IPlayerProgressionState _progression;
    private readonly IGameReportsService _gameReports;
    private readonly IActiveCharacterService _activeChar;

    public LiveProgressionAdapter(
        IPlayerProgressionState progression,
        IGameReportsService gameReports,
        IActiveCharacterService activeChar)
    {
        _progression = progression;
        _gameReports = gameReports;
        _activeChar = activeChar;

        _progression.StateChanged += OnSourceChanged;
        _gameReports.CharacterSnapshotsChanged += OnReportsChanged;
        _activeChar.ActiveCharacterChanged += OnActiveCharacterChanged;
    }

    /// <summary>Fires when live progression, exports, or active character identity changes.</summary>
    public event Action? Changed;

    /// <summary>How the current merged snapshot was produced.</summary>
    public ProgressionDataSource LastDataSource { get; private set; } = ProgressionDataSource.None;

    /// <summary>
    /// Returns a <see cref="CharacterSnapshot"/> for the active character, or null when
    /// neither live nor export data is available.
    /// </summary>
    public CharacterSnapshot? GetMergedSnapshot()
    {
        var name = _activeChar.ActiveCharacterName;
        var server = _activeChar.ActiveServer;
        var export = _gameReports.GetCharacterSnapshot(name, server);
        var liveSkills = _progression.Skills;
        var liveRecipes = _progression.RecipeCompletions;
        var hasLive = liveSkills.Count > 0 || liveRecipes.Count > 0;

        if (export is null && !hasLive)
        {
            LastDataSource = ProgressionDataSource.None;
            return null;
        }

        if (export is null)
        {
            LastDataSource = ProgressionDataSource.LiveOnly;
            return SynthesizeFromLive(name, server, liveSkills, liveRecipes);
        }

        if (!hasLive)
        {
            LastDataSource = ProgressionDataSource.ExportOnly;
            return export;
        }

        LastDataSource = ProgressionDataSource.Merged;
        return Merge(export, liveSkills, liveRecipes);
    }

    /// <summary>Export timestamp when an export backs the snapshot; otherwise null.</summary>
    public DateTimeOffset? ExportTimestamp
    {
        get
        {
            var export = _gameReports.GetCharacterSnapshot(
                _activeChar.ActiveCharacterName, _activeChar.ActiveServer);
            return export?.ExportedAt;
        }
    }

    /// <summary>Freshest live skill measurement; null when live skills are empty.</summary>
    public DateTimeOffset? LiveMeasuredAt
    {
        get
        {
            DateTimeOffset? latest = null;
            foreach (var skill in _progression.Skills.Values)
            {
                if (latest is null || skill.MeasuredAt > latest)
                    latest = skill.MeasuredAt;
            }
            return latest;
        }
    }

    public void Dispose()
    {
        _progression.StateChanged -= OnSourceChanged;
        _gameReports.CharacterSnapshotsChanged -= OnReportsChanged;
        _activeChar.ActiveCharacterChanged -= OnActiveCharacterChanged;
    }

    private void OnSourceChanged() => Changed?.Invoke();

    private void OnReportsChanged(object? sender, EventArgs e) => Changed?.Invoke();

    private void OnActiveCharacterChanged(object? sender, EventArgs e) => Changed?.Invoke();

    private static CharacterSnapshot Merge(
        CharacterSnapshot export,
        IReadOnlyDictionary<string, EnrichedSkill> liveSkills,
        IReadOnlyDictionary<string, int> liveRecipes)
    {
        var skills = new Dictionary<string, CharacterSkill>(export.Skills, StringComparer.Ordinal);
        foreach (var (key, enriched) in liveSkills)
            skills[key] = ToCharacterSkill(enriched);

        var recipes = new Dictionary<string, int>(export.RecipeCompletions, StringComparer.Ordinal);
        foreach (var (name, count) in liveRecipes)
            recipes[name] = count;

        return export with
        {
            Skills = skills,
            RecipeCompletions = recipes,
        };
    }

    private static CharacterSnapshot SynthesizeFromLive(
        string? name,
        string? server,
        IReadOnlyDictionary<string, EnrichedSkill> liveSkills,
        IReadOnlyDictionary<string, int> liveRecipes)
    {
        var skills = liveSkills.ToDictionary(
            kv => kv.Key,
            kv => ToCharacterSkill(kv.Value),
            StringComparer.Ordinal);

        var recipes = new Dictionary<string, int>(liveRecipes, StringComparer.Ordinal);

        DateTimeOffset exportedAt = DateTimeOffset.UtcNow;
        foreach (var skill in liveSkills.Values)
        {
            if (skill.MeasuredAt > exportedAt)
                exportedAt = skill.MeasuredAt;
        }

        return new CharacterSnapshot(
            name ?? "",
            server ?? "",
            exportedAt,
            skills,
            recipes,
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static CharacterSkill ToCharacterSkill(EnrichedSkill enriched)
        => new(enriched.Level, enriched.BonusLevels, enriched.CurrentXp, enriched.XpNeededForNextLevel);
}

/// <summary>Provenance of the last <see cref="LiveProgressionAdapter.GetMergedSnapshot"/> call.</summary>
public enum ProgressionDataSource
{
    None,
    ExportOnly,
    LiveOnly,
    Merged,
}
