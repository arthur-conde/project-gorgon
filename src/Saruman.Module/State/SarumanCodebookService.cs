using System.Text.RegularExpressions;
using Arda.Dispatch;
using Arda.World.Chat.Events;
using Arda.World.Player.Events;
using Mithril.Shared.Character;

namespace Saruman.State;

/// <summary>
/// Saruman's module-internal Word-of-Power codebook. Subscribes to Arda domain
/// events (<see cref="WordOfPowerDiscovered"/> from Player.log,
/// <see cref="PlayerChatLine"/> from ChatLogs) and maintains a persistent
/// per-character codebook via <see cref="PerCharacterView{T}"/>.
///
/// <para><b>Chat-spend detection.</b> Every player chat line is scanned for
/// uppercase tokens of length 4+. Tokens that match a known code in the
/// codebook flip that entry's <see cref="SarumanCodebook.CodebookEntry.LastSpentAt"/>
/// (monotonic — once set, never cleared by this service).</para>
///
/// <para><b>Persistence.</b> Discovery and spend-flip mutations are persisted
/// immediately. The <see cref="PerCharacterView{T}"/> handles character
/// switching (flush on switch, lazy-load for the new character).</para>
/// </summary>
public sealed partial class SarumanCodebookService : IDisposable
{
    [GeneratedRegex(@"\b[A-Z]{4,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex UpperTokenRx();

    private readonly PerCharacterView<SarumanCodebook> _view;
    private readonly IDisposable? _discoverSub;
    private readonly IDisposable? _chatLineSub;
    private readonly Lock _lock = new();

    public SarumanCodebookService(
        PerCharacterView<SarumanCodebook> view,
        IDomainEventSubscriber events)
    {
        _view = view;
        _view.CurrentChanged += (_, _) => CodebookChanged?.Invoke(this, EventArgs.Empty);
        _discoverSub = events.Subscribe<WordOfPowerDiscovered>(OnDiscovered);
        _chatLineSub = events.Subscribe<PlayerChatLine>(OnChatLine);
    }

    /// <summary>Fires on any mutation or character switch.</summary>
    public event EventHandler? CodebookChanged;

    /// <summary>
    /// Current character's codebook entries. Empty when no character is active.
    /// </summary>
    public IReadOnlyDictionary<string, SarumanCodebook.CodebookEntry> Entries
    {
        get
        {
            var state = _view.Current;
            if (state is null) return EmptyEntries;
            lock (_lock) return state.Entries;
        }
    }

    private static readonly IReadOnlyDictionary<string, SarumanCodebook.CodebookEntry> EmptyEntries =
        new Dictionary<string, SarumanCodebook.CodebookEntry>();

    private void OnDiscovered(WordOfPowerDiscovered evt)
    {
        var state = _view.Current;
        if (state is null) return;

        var code = evt.Code.ToString();
        var effect = evt.Effect.ToString();
        var desc = evt.Description.Length > 0 ? evt.Description.ToString() : null;
        var ts = evt.Metadata.Timestamp ?? evt.Metadata.ReadOn;

        bool changed;
        lock (_lock)
        {
            if (state.Entries.ContainsKey(code))
            {
                changed = false;
            }
            else
            {
                state.Entries[code] = new SarumanCodebook.CodebookEntry
                {
                    Code = code,
                    Effect = effect,
                    Description = desc,
                    DiscoveredAt = ts,
                    LastSpentAt = null,
                };
                Persist();
                changed = true;
            }
        }

        if (changed)
            CodebookChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnChatLine(PlayerChatLine evt)
    {
        if (string.IsNullOrEmpty(evt.Text)) return;

        var state = _view.Current;
        if (state is null) return;

        foreach (Match tok in UpperTokenRx().Matches(evt.Text))
        {
            var code = tok.Value;
            bool changed;
            lock (_lock)
            {
                if (!state.Entries.TryGetValue(code, out var entry))
                    continue;
                if (entry.LastSpentAt is not null)
                    continue;

                entry.LastSpentAt = evt.Metadata.Timestamp ?? evt.Metadata.ReadOn;
                Persist();
                changed = true;
            }

            if (changed)
                CodebookChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Persist()
    {
        try { _view.Save(); }
        catch { /* best-effort */ }
    }

    public void Dispose()
    {
        _discoverSub?.Dispose();
        _chatLineSub?.Dispose();
    }
}
