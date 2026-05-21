using System.Globalization;
using Mithril.Shared.Logging;

namespace Mithril.GameState.Sessions.Parsing;

/// <summary>
/// Parses the <c>EVENT(Ok): connected, url=&lt;host&gt;, port=&lt;port&gt;</c>
/// preamble line PG emits when the client establishes its game-server TCP
/// connection. Consumes the envelope-stripped payload from
/// <see cref="SystemSignalLogLine.Data"/> (L0.5 classifier eats the
/// <c>EVENT(Ok): </c> prefix per <see cref="SystemSignalKind.ConnectionEvent"/>),
/// so the parser sees the bare <c>connected, url=…, port=…</c> body.
///
/// <para>The grammar is structurally simpler than the <c>Servers:</c> JSON
/// blob — comma-delimited <c>key=value</c> pairs, no quoting, no nesting.
/// Tokenised with hand-rolled span splits; the cost profile (~one event
/// per session) is negligible either way, but the shape is uniform with
/// the other live-log idioms in this assembly.</para>
///
/// <para>Unrelated lines fast-path to <c>null</c>. Malformed input
/// (missing url, non-numeric port, extra/missing fields) also returns
/// <c>null</c> rather than throwing — the parser never throws on
/// production input. Defensively accepts the raw prefixed form
/// (<c>"EVENT(Ok): connected, …"</c>) for ad-hoc / test callers in
/// addition to the L0.5-stripped form.</para>
///
/// <para>Per #611, the host is preserved verbatim (no lowercasing) so a
/// catalog lookup reproduces PG's exact string;
/// <see cref="Mithril.GameState.Servers.IServerCatalogService.Get"/>
/// performs case-insensitive matching on its side.</para>
/// </summary>
public sealed class ConnectionEventParser : ILogParser
{
    // The stripped payload is "connected, url=<host>, port=<port>".
    // The prefixed form is "EVENT(Ok): connected, url=<host>, port=<port>".
    private const string StrippedPrefix = "connected,";
    private const string RawPrefix = "EVENT(Ok): connected,";

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(line)) return null;

        // Accept either the L0.5-stripped form (production path) or the raw
        // prefixed form (test fixtures, REPL probes). The body shape is
        // identical from "connected," onwards.
        ReadOnlySpan<char> body;
        if (line.StartsWith(StrippedPrefix, StringComparison.Ordinal))
        {
            body = line.AsSpan(StrippedPrefix.Length);
        }
        else if (line.StartsWith(RawPrefix, StringComparison.Ordinal))
        {
            body = line.AsSpan(RawPrefix.Length);
        }
        else
        {
            return null;
        }

        // body is now " url=<host>, port=<port>" (with a possible leading space).
        // Tolerate either ", " or "," between fields and any surrounding
        // whitespace — keep the parser shape-tolerant within the literal
        // grammar PG emits.
        string? url = null;
        int? port = null;

        var remaining = body;
        while (!remaining.IsEmpty)
        {
            // Find the next comma (or end-of-input).
            var commaIdx = remaining.IndexOf(',');
            ReadOnlySpan<char> field;
            if (commaIdx < 0)
            {
                field = remaining;
                remaining = ReadOnlySpan<char>.Empty;
            }
            else
            {
                field = remaining.Slice(0, commaIdx);
                remaining = remaining.Slice(commaIdx + 1);
            }
            field = field.Trim();
            if (field.IsEmpty) continue;

            var eq = field.IndexOf('=');
            if (eq <= 0) return null; // malformed pair, drop the whole event
            var key = field.Slice(0, eq).Trim();
            var value = field.Slice(eq + 1).Trim();

            if (key.SequenceEqual("url"))
            {
                if (value.IsEmpty) return null;
                url = value.ToString();
            }
            else if (key.SequenceEqual("port"))
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
                    return null;
                if (p <= 0 || p > 65535) return null;
                port = p;
            }
            // Unknown keys are ignored — a future PG patch could add fields
            // without breaking the parser. Strictness on required fields
            // (url + port) is checked after the enumeration.
        }

        if (url is null || port is null) return null;
        return new ConnectionEvent(timestamp, url, port.Value);
    }
}
