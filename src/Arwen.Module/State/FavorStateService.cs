using Arwen.Domain;
using Mithril.Shared.Character;
using Mithril.Shared.Reference;

namespace Arwen.State;

/// <summary>
/// Unified NPC favor state entry merging CDN metadata, character export tier, and persisted exact favor.
/// </summary>
public sealed class NpcFavorEntry
{
    public required string NpcKey { get; init; }
    public required string Name { get; init; }
    public required string Area { get; init; }
    public required IReadOnlyList<NpcPreference> Preferences { get; init; }
    public required IReadOnlyList<string> ItemGiftTiers { get; init; }

    /// <summary>Best-known favor tier (from exact favor if available, else character export).</summary>
    public FavorTier CurrentTier { get; set; } = FavorTier.Neutral;

    /// <summary>Exact absolute favor from Player.log, or null if unknown.</summary>
    public double? ExactFavor { get; set; }

    /// <summary>0.0–1.0 progress within current tier. NaN if tier has no cap or exact favor unknown.</summary>
    public double TierProgress { get; set; } = double.NaN;

    /// <summary>Whether this NPC appears in the character export (player has interacted with them).</summary>
    public bool IsKnown { get; set; }
}

/// <summary>
/// Merges three data sources into a unified NPC favor view:
/// 1. CDN NPC metadata (name, area, preferences)
/// 2. Character export tier (fallback)
/// 3. Persisted exact favor from Player.log (highest priority)
/// </summary>
public sealed class FavorStateService : IFavorLookupService
{
    private readonly IReferenceDataService _refData;
    private readonly IActiveCharacterService _charData;
    private readonly PerCharacterView<ArwenFavorState> _favorView;

    private IReadOnlyList<NpcFavorEntry> _entries = [];
    private Dictionary<string, FavorTier> _tierByNpcKey = new(StringComparer.Ordinal);

    public IReadOnlyList<NpcFavorEntry> Entries => _entries;

    public event EventHandler? StateChanged;
    public event EventHandler? FavorChanged;

    public string? GetFavorTier(string npcKey)
    {
        if (string.IsNullOrEmpty(npcKey)) return null;
        return _tierByNpcKey.TryGetValue(npcKey, out var tier) ? ToGameLogName(tier) : null;
    }

    // Arwen's enum uses "Hatred"; the game's log emits "Hated". Everything else already matches.
    private static string ToGameLogName(FavorTier tier) => tier switch
    {
        FavorTier.Hatred => "Hated",
        _ => tier.ToString(),
    };

    public FavorStateService(
        IReferenceDataService refData,
        IActiveCharacterService charData,
        PerCharacterView<ArwenFavorState> favorView)
    {
        _refData = refData;
        _charData = charData;
        _favorView = favorView;

        _refData.FileUpdated += (_, key) => { if (key == "npcs") Rebuild(); };
        _charData.CharacterExportsChanged += (_, _) => Rebuild();
        // CurrentChanged covers two triggers: character switch (fires from ActiveCharacterChanged
        // inside PerCharacterView) and post-fanout invalidate. One subscription is enough — no
        // separate ActiveCharacterChanged hookup needed.
        _favorView.CurrentChanged += (_, _) => Rebuild();
        Rebuild();
    }

    /// <summary>Called by the ingestion service when a single NPC's favor is updated.</summary>
    public void OnFavorUpdated(string npcKey)
    {
        // Update the specific entry in-place if it exists
        foreach (var entry in _entries)
        {
            if (entry.NpcKey != npcKey) continue;
            ApplyFavorData(entry);
            _tierByNpcKey[entry.NpcKey] = entry.CurrentTier;
            StateChanged?.Invoke(this, EventArgs.Empty);
            FavorChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        // NPC not in list yet — full rebuild
        Rebuild();
    }

    public void Rebuild()
    {
        var npcs = _refData.Npcs;
        var activeChar = _charData.ActiveCharacter;

        var entries = new List<NpcFavorEntry>(npcs.Count);

        foreach (var (key, npc) in npcs)
        {
            var entry = new NpcFavorEntry
            {
                NpcKey = key,
                Name = npc.Name,
                Area = npc.Area,
                Preferences = npc.Preferences,
                ItemGiftTiers = npc.ItemGiftTiers,
            };

            // Check if player knows this NPC (from character export)
            if (activeChar?.NpcFavor.ContainsKey(key) == true)
                entry.IsKnown = true;

            ApplyFavorData(entry);
            entries.Add(entry);
        }

        _entries = entries;
        _tierByNpcKey = entries
            .Where(e => e.IsKnown)
            .ToDictionary(e => e.NpcKey, e => e.CurrentTier, StringComparer.Ordinal);
        StateChanged?.Invoke(this, EventArgs.Empty);
        FavorChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyFavorData(NpcFavorEntry entry)
    {
        var activeChar = _charData.ActiveCharacter;

        // Priority 1: Persisted exact favor from Player.log
        var snapshot = _favorView.Current?.GetExactFavor(entry.NpcKey);
        if (snapshot is not null)
        {
            entry.ExactFavor = snapshot.ExactFavor;
            entry.CurrentTier = FavorTiers.TierForFavor(snapshot.ExactFavor);
            entry.TierProgress = FavorTiers.ProgressInTier(snapshot.ExactFavor, entry.CurrentTier);
            entry.IsKnown = true;
            return;
        }

        // Priority 2: Character export tier (no exact value)
        if (activeChar?.NpcFavor.TryGetValue(entry.NpcKey, out var tierName) == true &&
            FavorTiers.TryParse(tierName, out var tier))
        {
            entry.CurrentTier = tier;
            entry.ExactFavor = null;
            entry.TierProgress = double.NaN;
            entry.IsKnown = true;
            return;
        }

        // Priority 3: Unknown
        entry.CurrentTier = FavorTier.Neutral;
        entry.ExactFavor = null;
        entry.TierProgress = double.NaN;
    }
}
