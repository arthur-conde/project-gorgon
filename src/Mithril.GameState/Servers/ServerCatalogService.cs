using Mithril.GameState.Servers.Parsing;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Microsoft.Extensions.Hosting;

namespace Mithril.GameState.Servers;

/// <summary>
/// Hosted-service implementation of <see cref="IServerCatalogService"/>.
/// Subscribes to the L1 (#550) driver's unified classified pipe filtered
/// for <see cref="SystemSignalKind.Servers"/> envelopes (#610), parses each
/// via <see cref="ServerCatalogParser"/>, and folds the result into a
/// <c>Url → ServerEntry</c> dictionary.
///
/// <para><b>Last-writer-wins.</b> PG emits the catalog once per launch.
/// If the service observes a second emission (PG-restart-while-Mithril-
/// runs), the new catalog atomically replaces the prior one — the live
/// PG client's view is always more recent. Subsequent <see cref="Get"/>
/// calls reflect the new view immediately.</para>
///
/// <para><b>Reference-scope, no <c>Subscribe(…)</c>.</b> Unlike
/// <see cref="Mithril.GameState.Celestial.IPlayerCelestialState"/> or
/// <see cref="Mithril.GameState.Movement.IPlayerPositionTracker"/>, the
/// catalog is not live state consumers react to — it's a lookup table.
/// A future consumer that needs change notifications can add the same
/// replay-on-Subscribe pattern; #611 (ConnectionEventParser) does not need
/// it because connect-events arrive after the catalog is populated when
/// it's going to be populated at all.</para>
///
/// <para><b>Threading.</b> The L1 driver delivers envelopes on its pump
/// thread (archetype-A default = <c>DeliveryContext.Inline</c>); reads of
/// <see cref="All"/> and <see cref="Get"/> happen under <c>_lock</c>. Both
/// are O(1) / O(n) over a fixed-size catalog, so contention is negligible.</para>
/// </summary>
public sealed class ServerCatalogService : BackgroundService, IServerCatalogService
{
    private readonly ILogStreamDriver _driver;
    private readonly ServerCatalogParser _parser;
    private readonly IDiagnosticsSink? _diag;

    private readonly object _lock = new();
    // Url → entry, case-insensitive on lookup. The dictionary is replaced
    // wholesale on each parse so readers never see a partially-populated
    // catalog.
    private Dictionary<string, ServerEntry> _byUrl = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyCollection<ServerEntry> _allView = Array.Empty<ServerEntry>();
    private ILogSubscription? _subscription;

    private const string DiagCategory = "GameState.ServerCatalog";

    public ServerCatalogService(
        ILogStreamDriver driver,
        ServerCatalogParser parser,
        IDiagnosticsSink? diag = null)
    {
        _driver = driver;
        _parser = parser;
        _diag = diag;
    }

    /// <inheritdoc/>
    public ServerEntry? Get(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        lock (_lock)
        {
            return _byUrl.TryGetValue(url, out var entry) ? entry : null;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<ServerEntry> All
    {
        get { lock (_lock) return _allView; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _diag?.Info(DiagCategory,
            "Subscribing to L1 driver (SystemSignal pipe) for Servers: catalog line");
        // archetype-A defaults — FromSessionStart replay + Inline delivery.
        // The classifier strips "Servers: " so envelope.Payload.Data is the
        // bare JSON array body.
        _subscription = _driver.Subscribe<SystemSignalLogLine>(
            envelope =>
            {
                var signal = envelope.Payload;
                if (signal.Kind != SystemSignalKind.Servers) return ValueTask.CompletedTask;

                var ts = signal.Timestamp.UtcDateTime;
                if (_parser.TryParse(signal.Data, ts) is ServerCatalogEvent evt)
                {
                    UpdateCatalog(evt);
                    _diag?.Info(DiagCategory,
                        $"Parsed {evt.Entries.Count} server(s): " +
                        string.Join(", ", evt.Entries.Select(e => $"{e.Id}={e.Name}")));
                }
                else
                {
                    _diag?.Warn(DiagCategory,
                        $"Failed to parse Servers: payload (length {signal.Data.Length}); " +
                        "catalog unchanged. Sample: " +
                        (signal.Data.Length > 160 ? signal.Data.Substring(0, 160) + "…" : signal.Data));
                }

                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions
            {
                ReplayMode = ReplayMode.FromSessionStart,
                DeliveryContext = DeliveryContext.Inline,
                DiagnosticCategory = DiagCategory,
            });

        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on host stop */ }
        finally
        {
            _subscription?.Dispose();
            _subscription = null;
        }
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        base.Dispose();
    }

    private void UpdateCatalog(ServerCatalogEvent evt)
    {
        // Build the new dictionary outside the lock; swap atomically under
        // the lock. Readers always see either the old or the new map —
        // never a half-populated transition state.
        var next = new Dictionary<string, ServerEntry>(
            evt.Entries.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in evt.Entries)
        {
            // Duplicate URL would be a PG bug; last-wins is harmless.
            next[entry.Url] = entry;
        }
        var view = evt.Entries.ToArray();
        lock (_lock)
        {
            _byUrl = next;
            _allView = view;
        }
    }
}
