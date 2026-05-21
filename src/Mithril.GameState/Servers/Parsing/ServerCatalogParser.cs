using System.Text.Json;
using Mithril.Shared.Logging;

namespace Mithril.GameState.Servers.Parsing;

/// <summary>
/// Parses the <c>Servers: [ … ]</c> startup line PG emits after fetching
/// <c>clientconfig.json</c>. Consumes the envelope-stripped payload from
/// <see cref="SystemSignalLogLine.Data"/> (L0.5 classifier eats the
/// <c>Servers: </c> prefix per <see cref="SystemSignalKind.Servers"/>) so
/// the parser sees only the JSON array body:
///
/// <code>
/// [ { "AllowGuests" : true, "Port" : 9002, "Description" : "&lt;b&gt;Laeth - …&lt;/b&gt;\n…", "Url" : "s4.projectgorgon.com", "ID" : "s4", "Name" : "Laeth" }, … ]
/// </code>
///
/// <para>The JSON shape is PG-canonical — single line, all entries
/// present, fields not in a fixed order across entries. We use
/// <c>System.Text.Json</c> rather than the live-log regex idiom because:</para>
/// <list type="bullet">
///   <item>The payload is real JSON (not a verb-style <c>Verb(arg, arg)</c>
///   shape); a hand regex would have to re-implement string-escape handling.</item>
///   <item>Field ordering varies; a positional regex would be fragile.</item>
///   <item>The line is parsed once per attach, not per frame; allocation
///   cost is negligible.</item>
/// </list>
///
/// <para>Unrelated lines fast-path to <c>null</c>. Malformed JSON (truncated
/// payload, missing required fields, non-numeric port) returns <c>null</c>
/// rather than throwing — the parser never throws on production input.
/// All-or-nothing semantics: if any entry fails to parse, the whole event
/// is dropped. PG emits the catalog atomically; a partial parse would be
/// misleading.</para>
/// </summary>
public sealed class ServerCatalogParser : ILogParser
{
    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(line)) return null;

        // The L0.5 classifier strips the "Servers: " prefix for SystemSignal
        // payloads, so the production caller passes the bare "[ { … } ]"
        // body. Defensively accept the prefixed form too — test fixtures
        // and ad-hoc callers occasionally pass the raw line.
        var payload = line;
        if (line.StartsWith("Servers: ", StringComparison.Ordinal))
        {
            payload = line.Substring("Servers: ".Length);
        }

        var trimmed = payload.AsSpan().TrimStart();
        if (trimmed.IsEmpty || trimmed[0] != '[') return null;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var entries = new List<ServerEntry>(doc.RootElement.GetArrayLength());
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return null;
                if (!TryReadEntry(item, out var entry)) return null;
                entries.Add(entry);
            }

            return new ServerCatalogEvent(timestamp, entries);
        }
        catch (JsonException)
        {
            // Truncated payload, embedded control character PG escaped
            // incorrectly, etc. Treat as "not a catalog line".
            return null;
        }
    }

    private static bool TryReadEntry(JsonElement obj, out ServerEntry entry)
    {
        entry = null!;

        if (!obj.TryGetProperty("ID", out var idEl) || idEl.ValueKind != JsonValueKind.String) return false;
        if (!obj.TryGetProperty("Name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) return false;
        if (!obj.TryGetProperty("Url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String) return false;
        if (!obj.TryGetProperty("Port", out var portEl) || portEl.ValueKind != JsonValueKind.Number) return false;
        if (!portEl.TryGetInt32(out var port)) return false;
        // Description is allowed to be absent or empty — PG always emits it
        // today, but treat it as best-effort metadata rather than a hard
        // requirement so a future PG patch that drops the field doesn't
        // break the catalog.
        var description = obj.TryGetProperty("Description", out var descEl)
                            && descEl.ValueKind == JsonValueKind.String
            ? descEl.GetString() ?? string.Empty
            : string.Empty;

        var id = idEl.GetString();
        var name = nameEl.GetString();
        var url = urlEl.GetString();
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
            return false;

        entry = new ServerEntry(id, name, url, port, description);
        return true;
    }
}
