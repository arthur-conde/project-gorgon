using Arda.Composition.Events;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Microsoft.Extensions.Logging;
using Mithril.Reference.Models.Npcs;
using Mithril.Shared.Character;

namespace Arda.Composition.Internal;

/// <summary>
/// L4 composer that accumulates per-NPC state (absolute favor, favor tier, vendor
/// gold, gold reset time) from domain events and persists it via
/// <see cref="PerCharacterStore{T}"/>. Exposes <see cref="INpcStateTracker"/> for
/// downstream consumers (Arwen, Smaug).
/// </summary>
internal sealed class NpcStateComposer : INpcStateTracker, IDisposable
{
    private readonly IDomainEventPublisher _publisher;
    private readonly PerCharacterStore<NpcStateSnapshot>? _store;
    private readonly IGrammarBreakSignal? _grammarSignal;
    private readonly ILogger? _logger;

    private readonly Dictionary<string, NpcRecord> _npcs = new(StringComparer.Ordinal);
    private string? _currentCharacter;
    private string? _currentServer;
    private string? _currentVendorNpcKey;

    private IDisposable? _interactionSub;
    private IDisposable? _vendorScreenSub;
    private IDisposable? _vendorGoldSub;
    private IDisposable? _giftSub;
    private IDisposable? _sessionSub;

    public IReadOnlyDictionary<string, NpcRecord> Npcs => _npcs;
    public event Action? StateChanged;

    public NpcStateComposer(
        IDomainEventSubscriber subscriber,
        IDomainEventPublisher publisher,
        PerCharacterStore<NpcStateSnapshot>? store = null,
        IGrammarBreakSignal? grammarSignal = null,
        ILogger? logger = null)
    {
        _publisher = publisher;
        _store = store;
        _grammarSignal = grammarSignal;
        _logger = logger;

        _interactionSub = subscriber.Subscribe<InteractionStarted>(OnInteractionStarted);
        _vendorScreenSub = subscriber.Subscribe<VendorScreenOpened>(OnVendorScreenOpened);
        _vendorGoldSub = subscriber.Subscribe<VendorGoldUpdated>(OnVendorGoldUpdated);
        _giftSub = subscriber.Subscribe<GiftAccepted>(OnGiftAccepted);
        _sessionSub = subscriber.Subscribe<SessionEstablished>(OnSessionEstablished);
    }

    public NpcRecord? GetNpc(string npcKey)
        => _npcs.TryGetValue(npcKey, out var record) ? record : null;

    // ── Event handlers ─────────────────────────────────────────────────────

    private void OnInteractionStarted(InteractionStarted e)
    {
        if (!e.IsNpc || string.IsNullOrEmpty(e.Name))
            return;

        var now = e.Metadata.Timestamp ?? e.Metadata.ReadOn;
        var npcKey = e.Name;

        if (_npcs.TryGetValue(npcKey, out var existing))
        {
            _npcs[npcKey] = existing with
            {
                AbsoluteFavor = e.Favor,
                FavorUpdatedAt = now,
                LastSeenAt = now
            };
        }
        else
        {
            _npcs[npcKey] = new NpcRecord(
                NpcKey: npcKey,
                AbsoluteFavor: e.Favor,
                FavorUpdatedAt: now,
                FavorTier: FavorTier.Unknown,
                RemainingGold: null,
                GoldCap: null,
                GoldResetsAt: null,
                GoldUpdatedAt: null,
                LastSeenAt: now);
        }

        _publisher.Publish(new NpcStateChanged(npcKey, _npcs[npcKey], e.Metadata));
        StateChanged?.Invoke();
    }

    private void OnVendorScreenOpened(VendorScreenOpened e)
    {
        if (e.NpcKey is null)
            return;

        var now = e.Metadata.Timestamp ?? e.Metadata.ReadOn;
        var npcKey = e.NpcKey;
        _currentVendorNpcKey = npcKey;
        var tier = FavorTierExtensions.Parse(e.FavorTier);

        if (_npcs.TryGetValue(npcKey, out var existing))
        {
            _npcs[npcKey] = existing with
            {
                FavorTier = tier,
                RemainingGold = e.RemainingGold,
                GoldCap = e.GoldCap,
                GoldResetsAt = e.GoldResetsAt,
                GoldUpdatedAt = now,
                LastSeenAt = now
            };
        }
        else
        {
            _npcs[npcKey] = new NpcRecord(
                NpcKey: npcKey,
                AbsoluteFavor: null,
                FavorUpdatedAt: null,
                FavorTier: tier,
                RemainingGold: e.RemainingGold,
                GoldCap: e.GoldCap,
                GoldResetsAt: e.GoldResetsAt,
                GoldUpdatedAt: now,
                LastSeenAt: now);
        }

        _publisher.Publish(new NpcStateChanged(npcKey, _npcs[npcKey], e.Metadata));
        StateChanged?.Invoke();
    }

    private void OnVendorGoldUpdated(VendorGoldUpdated e)
    {
        if (_currentVendorNpcKey is null)
            return;

        var now = e.Metadata.Timestamp ?? e.Metadata.ReadOn;
        var npcKey = _currentVendorNpcKey;

        if (_npcs.TryGetValue(npcKey, out var existing))
        {
            _npcs[npcKey] = existing with
            {
                RemainingGold = e.RemainingGold,
                GoldCap = e.GoldCap,
                GoldResetsAt = e.GoldResetsAt,
                GoldUpdatedAt = now,
                LastSeenAt = now
            };
        }
        else
        {
            _npcs[npcKey] = new NpcRecord(
                NpcKey: npcKey,
                AbsoluteFavor: null,
                FavorUpdatedAt: null,
                FavorTier: FavorTier.Unknown,
                RemainingGold: e.RemainingGold,
                GoldCap: e.GoldCap,
                GoldResetsAt: e.GoldResetsAt,
                GoldUpdatedAt: now,
                LastSeenAt: now);
        }

        _publisher.Publish(new NpcStateChanged(npcKey, _npcs[npcKey], e.Metadata));
        StateChanged?.Invoke();
    }

    private void OnGiftAccepted(GiftAccepted e)
    {
        var now = e.Metadata.Timestamp ?? e.Metadata.ReadOn;
        var npcKey = e.NpcKey;

        if (_npcs.TryGetValue(npcKey, out var existing))
        {
            var newFavor = (existing.AbsoluteFavor ?? 0) + e.DeltaFavor;
            _npcs[npcKey] = existing with
            {
                AbsoluteFavor = newFavor,
                FavorUpdatedAt = now,
                LastSeenAt = now
            };
        }
        else
        {
            _npcs[npcKey] = new NpcRecord(
                NpcKey: npcKey,
                AbsoluteFavor: e.DeltaFavor,
                FavorUpdatedAt: now,
                FavorTier: FavorTier.Unknown,
                RemainingGold: null,
                GoldCap: null,
                GoldResetsAt: null,
                GoldUpdatedAt: null,
                LastSeenAt: now);
        }

        _publisher.Publish(new NpcStateChanged(npcKey, _npcs[npcKey], e.Metadata));
        StateChanged?.Invoke();
    }

    // ── Session / persistence ──────────────────────────────────────────────

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

    private void FlushToDisk()
    {
        if (_store is null || _currentCharacter is null || _currentServer is null)
            return;

        if (_grammarSignal?.HasObservedBreak == true)
        {
            _logger?.LogWarning(
                "Skipping NPC state snapshot save for {Character}/{Server}: grammar break observed in this session",
                _currentCharacter, _currentServer);
            return;
        }

        var snapshot = new NpcStateSnapshot();
        foreach (var (key, record) in _npcs)
        {
            snapshot.Npcs[key] = new NpcStateSnapshot.PersistedNpc
            {
                AbsoluteFavor = record.AbsoluteFavor,
                FavorUpdatedAt = record.FavorUpdatedAt,
                // Persisted as the canonical token string (round-trip-safe with
                // existing on-disk files). Unknown round-trips to "Unknown".
                FavorTier = record.FavorTier.ToToken(),
                RemainingGold = record.RemainingGold,
                GoldCap = record.GoldCap,
                GoldResetsAt = record.GoldResetsAt,
                GoldUpdatedAt = record.GoldUpdatedAt,
                LastSeenAt = record.LastSeenAt
            };
        }

        try
        {
            _store.Save(_currentCharacter, _currentServer, snapshot);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save NPC state snapshot for {Character}/{Server}",
                _currentCharacter, _currentServer);
        }
    }

    private void LoadFromDisk()
    {
        _npcs.Clear();
        _currentVendorNpcKey = null;

        if (_store is null || _currentCharacter is null || _currentServer is null)
        {
            StateChanged?.Invoke();
            return;
        }

        try
        {
            var snapshot = _store.Load(_currentCharacter, _currentServer);
            foreach (var (key, persisted) in snapshot.Npcs)
            {
                _npcs[key] = new NpcRecord(
                    NpcKey: key,
                    AbsoluteFavor: persisted.AbsoluteFavor,
                    FavorUpdatedAt: persisted.FavorUpdatedAt,
                    // Parse: case-insensitive, null/blank/junk → FavorTier.Unknown.
                    // Old snapshots written with string tokens round-trip cleanly.
                    FavorTier: FavorTierExtensions.Parse(persisted.FavorTier),
                    RemainingGold: persisted.RemainingGold,
                    GoldCap: persisted.GoldCap,
                    GoldResetsAt: persisted.GoldResetsAt,
                    GoldUpdatedAt: persisted.GoldUpdatedAt,
                    LastSeenAt: persisted.LastSeenAt);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load NPC state snapshot for {Character}/{Server}",
                _currentCharacter, _currentServer);
        }

        StateChanged?.Invoke();
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        FlushToDisk();
        _interactionSub?.Dispose();
        _vendorScreenSub?.Dispose();
        _vendorGoldSub?.Dispose();
        _giftSub?.Dispose();
        _sessionSub?.Dispose();
        _interactionSub = null;
        _vendorScreenSub = null;
        _vendorGoldSub = null;
        _giftSub = null;
        _sessionSub = null;
    }
}
