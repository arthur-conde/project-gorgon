using Arda.Composition.Events;
using Arda.Dispatch;
using Arda.World.Player;
using Arda.World.Player.Events;
using Microsoft.Extensions.Logging;
using Mithril.Shared.Character;

namespace Arda.Composition.Internal;

/// <summary>
/// L4 persistent composer that tracks live skill and recipe state from the
/// Arda player world. Persists per-character via <see cref="PerCharacterStore{T}"/>
/// so data survives restarts and log rotation. Publishes
/// <see cref="SkillProgressionChanged"/> for per-skill deltas and exposes
/// <see cref="IPlayerProgressionState"/> for snapshot queries.
/// </summary>
internal sealed class PlayerProgressionComposer : IPlayerProgressionState, IDisposable
{
    private readonly IDomainEventBus _bus;
    private readonly IPlayerState _playerState;
    private readonly PerCharacterStore<ProgressionSnapshot>? _store;
    private readonly Func<int, string?>? _recipeKeyResolver;
    private readonly IGrammarBreakSignal? _grammarSignal;
    private readonly ILogger? _logger;

    // ── Live state ────────────────────────────────────────────────────────
    private Dictionary<string, EnrichedSkill> _skills = new(StringComparer.Ordinal);
    private Dictionary<string, int> _recipes = new(StringComparer.Ordinal);
    private string? _currentCharacter;
    private string? _currentServer;

    // ── Subscriptions ─────────────────────────────────────────────────────
    private IDisposable? _skillsLoadedSub;
    private IDisposable? _skillUpdatedSub;
    private IDisposable? _recipesLoadedSub;
    private IDisposable? _recipeUpdatedSub;
    private IDisposable? _sessionSub;

    public IReadOnlyDictionary<string, EnrichedSkill> Skills => _skills;
    public IReadOnlyDictionary<string, int> RecipeCompletions => _recipes;
    public event Action? StateChanged;

    public PlayerProgressionComposer(
        IDomainEventBus bus,
        IPlayerState playerState,
        PerCharacterStore<ProgressionSnapshot>? store = null,
        Func<int, string?>? recipeKeyResolver = null,
        IGrammarBreakSignal? grammarSignal = null,
        ILogger? logger = null)
    {
        _bus = bus;
        _playerState = playerState;
        _store = store;
        _recipeKeyResolver = recipeKeyResolver;
        _grammarSignal = grammarSignal;
        _logger = logger;

        _skillsLoadedSub = bus.Subscribe<SkillsLoaded>(OnSkillsLoaded);
        _skillUpdatedSub = bus.Subscribe<SkillUpdated>(OnSkillUpdated);
        _recipesLoadedSub = bus.Subscribe<RecipesLoaded>(OnRecipesLoaded);
        _recipeUpdatedSub = bus.Subscribe<RecipeUpdated>(OnRecipeUpdated);
        _sessionSub = bus.Subscribe<SessionEstablished>(OnSessionEstablished);
    }

    // ── Skill events ──────────────────────────────────────────────────────

    private void OnSkillsLoaded(SkillsLoaded evt)
    {
        var measuredAt = evt.Metadata.Timestamp ?? evt.Metadata.ReadOn;
        var newDict = new Dictionary<string, EnrichedSkill>(_playerState.Skills.Count, StringComparer.Ordinal);

        foreach (var (key, entry) in _playerState.Skills)
        {
            newDict[key] = new EnrichedSkill(
                key,
                entry.Raw,
                entry.Bonus,
                entry.Xp,
                entry.Tnl,
                entry.Max,
                IsCapped: entry.Raw >= entry.Max && entry.Max > 0,
                measuredAt);
        }

        _skills = newDict;
        StateChanged?.Invoke();
    }

    private void OnSkillUpdated(SkillUpdated evt)
    {
        var measuredAt = evt.Metadata.Timestamp ?? evt.Metadata.ReadOn;
        var enriched = new EnrichedSkill(
            evt.SkillKey,
            evt.Raw,
            evt.Bonus,
            evt.Xp,
            evt.Tnl,
            evt.Max,
            IsCapped: evt.Raw >= evt.Max && evt.Max > 0,
            measuredAt);

        var newDict = new Dictionary<string, EnrichedSkill>(_skills, StringComparer.Ordinal)
        {
            [evt.SkillKey] = enriched
        };
        _skills = newDict;

        _bus.Publish(new SkillProgressionChanged(evt.SkillKey, enriched, evt.XpGained, evt.Metadata));
        StateChanged?.Invoke();
    }

    // ── Recipe events ─────────────────────────────────────────────────────

    private void OnRecipesLoaded(RecipesLoaded evt)
    {
        var newDict = new Dictionary<string, int>(_playerState.Recipes.Count, StringComparer.Ordinal);

        foreach (var (_, entry) in _playerState.Recipes)
        {
            var name = ResolveRecipeKey(entry.RecipeId);
            if (name is not null)
                newDict[name] = entry.Count;
        }

        _recipes = newDict;
        StateChanged?.Invoke();
    }

    private void OnRecipeUpdated(RecipeUpdated evt)
    {
        var name = ResolveRecipeKey(evt.RecipeId);
        if (name is null)
            return;

        var newDict = new Dictionary<string, int>(_recipes, StringComparer.Ordinal)
        {
            [name] = evt.Count
        };
        _recipes = newDict;

        StateChanged?.Invoke();
    }

    private string? ResolveRecipeKey(int recipeId)
    {
        if (_recipeKeyResolver is not null)
            return _recipeKeyResolver(recipeId);

        return $"recipe_{recipeId}";
    }

    // ── Session / persistence ─────────────────────────────────────────────

    private void OnSessionEstablished(SessionEstablished evt)
    {
        var session = evt.Session;
        if (session.CharacterName == _currentCharacter && session.Server == _currentServer)
            return;

        FlushToDisk();
        _currentCharacter = session.CharacterName;
        _currentServer = session.Server;
        LoadFromDisk();
    }

    private bool CanPersist =>
        _store is not null
        && !string.IsNullOrEmpty(_currentCharacter)
        && !string.IsNullOrEmpty(_currentServer);

    private void FlushToDisk()
    {
        if (!CanPersist)
            return;

        if (_grammarSignal?.HasObservedBreak == true)
        {
            _logger?.LogWarning(
                "Skipping progression snapshot save for {Character}/{Server}: grammar break observed in this session",
                _currentCharacter, _currentServer);
            return;
        }

        var store = _store!;
        var character = _currentCharacter!;
        var server = _currentServer!;

        var snapshot = new ProgressionSnapshot();
        foreach (var (key, skill) in _skills)
        {
            snapshot.Skills[key] = new ProgressionSnapshot.PersistedSkill
            {
                Level = skill.Level,
                BonusLevels = skill.BonusLevels,
                CurrentXp = skill.CurrentXp,
                XpNeededForNextLevel = skill.XpNeededForNextLevel,
                MaxLevel = skill.MaxLevel,
                MeasuredAt = skill.MeasuredAt
            };
        }

        foreach (var (name, count) in _recipes)
            snapshot.RecipeCompletions[name] = count;

        try
        {
            store.Save(character, server, snapshot);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save progression snapshot for {Character}/{Server}",
                character, server);
        }
    }

    private void LoadFromDisk()
    {
        if (!CanPersist)
        {
            StateChanged?.Invoke();
            return;
        }

        var store = _store!;
        var character = _currentCharacter!;
        var server = _currentServer!;

        try
        {
            var snapshot = store.Load(character, server);
            MergePersistedBaseline(snapshot);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load progression snapshot for {Character}/{Server}",
                character, server);
        }

        StateChanged?.Invoke();
    }

    /// <summary>
    /// Merges persisted baseline with current live state. Skills already seen
    /// in replay keep their live values; skills only in the snapshot fill gaps
    /// (e.g. after log rotation).
    /// </summary>
    private void MergePersistedBaseline(ProgressionSnapshot snapshot)
    {
        var mergedSkills = new Dictionary<string, EnrichedSkill>(_skills, StringComparer.Ordinal);
        foreach (var (key, persisted) in snapshot.Skills)
        {
            if (mergedSkills.ContainsKey(key))
                continue;

            mergedSkills[key] = new EnrichedSkill(
                key,
                persisted.Level,
                persisted.BonusLevels,
                persisted.CurrentXp,
                persisted.XpNeededForNextLevel,
                persisted.MaxLevel,
                IsCapped: persisted.Level >= persisted.MaxLevel && persisted.MaxLevel > 0,
                persisted.MeasuredAt);
        }
        _skills = mergedSkills;

        var mergedRecipes = new Dictionary<string, int>(_recipes, StringComparer.Ordinal);
        foreach (var (name, count) in snapshot.RecipeCompletions)
        {
            if (!mergedRecipes.ContainsKey(name))
                mergedRecipes[name] = count;
        }
        _recipes = mergedRecipes;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────

    public void Dispose()
    {
        FlushToDisk();
        _skillsLoadedSub?.Dispose();
        _skillUpdatedSub?.Dispose();
        _recipesLoadedSub?.Dispose();
        _recipeUpdatedSub?.Dispose();
        _sessionSub?.Dispose();
        _skillsLoadedSub = null;
        _skillUpdatedSub = null;
        _recipesLoadedSub = null;
        _recipeUpdatedSub = null;
        _sessionSub = null;
    }
}
