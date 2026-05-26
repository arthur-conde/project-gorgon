using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.Composition.Internal;

/// <summary>
/// Tracks discovered Words of Power from <see cref="WordOfPowerDiscovered"/>
/// (player log). Maintains a per-session codebook. Chat-line utterance
/// scanning for spent-state tracking is deferred to module migration.
/// </summary>
internal sealed class WordOfPowerComposer : IWordOfPowerComposer, IDisposable
{
    private readonly Dictionary<string, WordOfPowerEntry> _words = new(StringComparer.OrdinalIgnoreCase);
    private IDisposable? _discoverSub;

    public IReadOnlyDictionary<string, WordOfPowerEntry> Words => _words;

    public WordOfPowerComposer(IDomainEventBus bus)
    {
        _discoverSub = bus.Subscribe<WordOfPowerDiscovered>(OnDiscovered);
    }

    private void OnDiscovered(WordOfPowerDiscovered evt)
    {
        var code = evt.Code.ToString();
        var effect = evt.Effect.ToString();
        var desc = evt.Description.Length > 0 ? evt.Description.ToString() : null;
        var ts = evt.Metadata.Timestamp ?? evt.Metadata.ReadOn;

        _words[code] = new WordOfPowerEntry(code, effect, desc, ts, IsSpent: false);
    }

    public void Dispose()
    {
        _discoverSub?.Dispose();
        _discoverSub = null;
    }
}
