using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Arda.Composition;
using Arda.Contracts;
using Arda.World.Chat.Events;
using Arda.World.Player.Events;

namespace Saruman.State;

/// <summary>
/// Saruman's module-internal Word-of-Power codebook. Subscribes to Arda domain
/// events (<see cref="WordOfPowerDiscovered"/> from Player.log,
/// <see cref="PlayerChatLine"/> from ChatLogs) and maintains a persistent
/// server-scoped codebook in a single JSON file.
///
/// <para><b>Server scoping.</b> WoPs are shared across all characters on the
/// same server. The active server is read from <see cref="ISessionComposer"/>;
/// entries are filtered accordingly when exposed via <see cref="Entries"/>.</para>
///
/// <para><b>Chat-spend detection.</b> Every player chat line is scanned for
/// uppercase tokens of length 4+. Tokens matching a known code on the active
/// server flip that entry's <see cref="SarumanCodebook.CodebookEntry.LastSpentAt"/>
/// (monotonic — once set, never cleared by this service).</para>
/// </summary>
public sealed partial class SarumanCodebookService : IDisposable
{
    [GeneratedRegex(@"\b[A-Z]{4,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex UpperTokenRx();

    private readonly string _filePath;
    private readonly ISessionComposer _session;
    private readonly IDisposable? _discoverSub;
    private readonly IDisposable? _chatLineSub;
    private readonly Lock _lock = new();
    private SarumanCodebook _codebook;

    public SarumanCodebookService(
        string filePath,
        ISessionComposer session,
        IDomainEventSubscriber events,
        SarumanCodebookLegacyMigration? legacyMigration = null)
    {
        _filePath = filePath;
        _session = session;
        _codebook = Load(filePath);

        if (_codebook.Entries.Count == 0 && legacyMigration is not null)
        {
            var recovered = legacyMigration.RecoverAll();
            if (recovered.Count > 0)
            {
                _codebook.Entries.AddRange(recovered);
                Save();
            }
        }

        _discoverSub = events.Subscribe<WordOfPowerDiscovered>(OnDiscovered);
        _chatLineSub = events.Subscribe<PlayerChatLine>(OnChatLine);
    }

    /// <summary>Fires on any mutation.</summary>
    public event EventHandler? CodebookChanged;

    /// <summary>
    /// All codebook entries keyed by code (case-insensitive). When entries exist
    /// on multiple servers, all are included. Server filtering is handled at the
    /// view layer via the query bar.
    /// </summary>
    public IReadOnlyDictionary<string, SarumanCodebook.CodebookEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _codebook.Entries
                    .ToDictionary(e => e.Code, e => e, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    private void OnDiscovered(WordOfPowerDiscovered evt)
    {
        var server = _session.Current?.Server;
        if (server is null) return;

        var code = evt.Code.ToString();
        var effect = evt.Effect.ToString();
        var desc = evt.Description.Length > 0 ? evt.Description.ToString() : null;
        var ts = evt.Metadata.Timestamp ?? evt.Metadata.ReadOn;

        bool changed;
        lock (_lock)
        {
            if (_codebook.Entries.Any(e =>
                string.Equals(e.Server, server, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase)))
            {
                changed = false;
            }
            else
            {
                _codebook.Entries.Add(new SarumanCodebook.CodebookEntry
                {
                    Server = server,
                    Code = code,
                    Effect = effect,
                    Description = desc,
                    DiscoveredAt = ts,
                    LastSpentAt = null,
                });
                Save();
                changed = true;
            }
        }

        if (changed)
            CodebookChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnChatLine(PlayerChatLine evt)
    {
        if (evt.Text.IsEmpty) return;

        var server = _session.Current?.Server;
        if (server is null) return;

        var textString = evt.Text.ToString();
        foreach (Match tok in UpperTokenRx().Matches(textString))
        {
            var code = tok.Value;
            bool changed;
            lock (_lock)
            {
                var entry = _codebook.Entries.FirstOrDefault(e =>
                    string.Equals(e.Server, server, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase));

                if (entry is null || entry.LastSpentAt is not null)
                    continue;

                entry.LastSpentAt = evt.Metadata.Timestamp ?? evt.Metadata.ReadOn;
                Save();
                changed = true;
            }

            if (changed)
                CodebookChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Seed entries from a legacy migration. Skips entries that already exist
    /// for the given server+code pair.
    /// </summary>
    internal void SeedFromLegacy(IEnumerable<SarumanCodebook.CodebookEntry> entries)
    {
        bool any = false;
        lock (_lock)
        {
            foreach (var entry in entries)
            {
                if (_codebook.Entries.Any(e =>
                    string.Equals(e.Server, entry.Server, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Code, entry.Code, StringComparison.OrdinalIgnoreCase)))
                    continue;

                _codebook.Entries.Add(entry);
                any = true;
            }

            if (any) Save();
        }

        if (any)
            CodebookChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (dir is not null) Directory.CreateDirectory(dir);
            using var stream = File.Create(_filePath);
            JsonSerializer.Serialize(stream, _codebook, SarumanCodebookJsonContext.Default.SarumanCodebook);
        }
        catch { /* best-effort */ }
    }

    private static SarumanCodebook Load(string path)
    {
        if (!File.Exists(path)) return new SarumanCodebook();
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, SarumanCodebookJsonContext.Default.SarumanCodebook)
                   ?? new SarumanCodebook();
        }
        catch { return new SarumanCodebook(); }
    }

    public void Dispose()
    {
        _discoverSub?.Dispose();
        _chatLineSub?.Dispose();
    }
}
