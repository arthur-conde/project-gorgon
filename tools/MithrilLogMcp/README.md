# MithrilLogMcp

MCP server exposing Mithril's log sources (Project Gorgon `Player.log`, `ChatLogs/`, and Mithril's own Serilog output) to Claude Code with parsed/typed events, time-windowed queries, and server-side aggregations. Designed to keep the cost of AI-assisted log analysis bounded — instead of having Claude `grep` and `Read` hundreds of MB, the server returns structured events and summaries.

## Status

- **v0.1** — Player.log only, `query_events`/`aggregate`/`list_event_types`, time windows. ✅
- **v0.2** — chat log + Mithril Serilog sources, `source: "all"` merge, field projection. ✅
- **v0.3** — persistent cursors, character scoping, `--last-session`, rollover detection. ✅
- **v0.4** — cursor advancement on successful queries (`cursor: "name"` resumes where the previous call stopped). ✅
- **v0.5** — active-character streaming: `character: "Foo"` filters Player.log events using a tracker that watches `ProcessAddPlayer` mid-stream and is backfilled from a backward scan on cursor resume. ✅

## Example queries

These show the typical shapes Claude should reach for. All run as MCP tool calls.

**1. Top NPCs the player interacted with in the last hour**

```jsonc
// tool: aggregate
{
  "source": "player",
  "agg": "top",
  "field": "npcKey",
  "event_type": ["arwen.FavorUpdate", "smaug.NpcInteractionStarted"],
  "since": "1h",
  "top_n": 10
}
```
Returns ten `{ key, count }` buckets — orders of magnitude smaller than the raw events.

**2. Everything Emraell did in their most recent session**

```jsonc
// tool: query_events
{
  "source": "player",
  "character": "Emraell",
  "last_session": true,
  "event_type": ["arwen.FavorUpdate", "smaug.VendorItemSold", "samwise.PlantingCapReached"],
  "limit": 200
}
```
The server resolves "last session" by walking back through Player.log to the most recent `ProcessAddPlayer` for that character; events are stamped with `activeCharacter` so cross-character noise is filtered out.

**3. Multi-turn investigation: keep a cursor and only see new events**

```jsonc
// turn 1 — establishes cursor "debug-vendor-bug"
{ "source": "player", "cursor": "debug-vendor-bug", "event_type": ["smaug.VendorItemSold"] }

// turn 2, after the player sells more — only new events
{ "source": "player", "cursor": "debug-vendor-bug", "event_type": ["smaug.VendorItemSold"] }
```
Second call scans only the bytes appended since turn 1. Rollover detection (truncation, file recreate) automatically resets to byte 0 and reports it in `summary.cursor.rolledOverFiles`.

**4. Context lines around each match (Player.log)**

```jsonc
// tool: query_events
{
  "source": "player",
  "event_type": ["samwise.PlantingCapReached"],
  "since": "30m",
  "context": 3
}
```
Each event gains `contextLines: { before: [...], after: [...] }` with up to 3 raw lines on each side.

## Tool surface

| Tool | Purpose |
|------|---------|
| `query_events` | Stream typed events filtered by source, event type, time window, character, or arbitrary `data.*` field |
| `aggregate` | Server-side `count` / `group_by` / `histogram` / `distinct` / `top` reductions |
| `list_event_types` | Schema discovery: every event type the server can emit, with field names |
| `cursor_list` | List named cursors stored at `%LocalAppData%/MithrilLogMcp/cursors.json` |
| `cursor_reset` | Remove a named cursor so the next query starts fresh |

## Sources

| Source | Path | Notes |
|--------|------|-------|
| `player` | `%LocalAppData%Low/Elder Game/Project Gorgon/Player.log` | Lines carry only `[HH:MM:SS]`; date is anchored by file mtime |
| `chat` | `%LocalAppData%Low/Elder Game/Project Gorgon/ChatLogs/Chat-YY-MM-DD.log` | Lines carry full `YY-MM-DD HH:MM:SS`; files outside the window are skipped without opening |
| `mithril` | `%LocalAppData%/Mithril/Shell/logs/*.json` | CompactJsonFormatter (`@t`, `@mt`, `Category`, `Message`); detection is by content, so legacy `gorgon-*.json` and migrated `mithril-*-prebrand.json` are also picked up |
| `all` | merged | 3-way time-ordered merge across the above |

Each can be overridden via env var (`MITHRIL_PLAYER_LOG`, `MITHRIL_CHAT_LOG_DIR`, `MITHRIL_DIAGNOSTIC_LOG_DIR`, `MITHRIL_CHARACTER_ROOT`, `MITHRIL_SHELL_SETTINGS`).

## Building & running

```bash
cd tools/MithrilLogMcp
npm install
npm run build
npm test
```

## Wiring into Claude Code

Add to your `.mcp.json`:

```jsonc
{
  "mcpServers": {
    "mithril-logs": {
      "command": "node",
      "args": ["${workspaceFolder}/tools/MithrilLogMcp/dist/src/server.js"]
    }
  }
}
```

## Architecture

The server reads `src/Mithril.Shared/Reference/log-patterns.json` at startup — the same regex catalog Mithril's .NET parsers consume. Pattern parity is enforced by `LogPatternCatalogParityTests` in the .NET test suite, so adding a new event type means editing one JSON file and one C# parser file.

See [docs/architecture.md](docs/architecture.md) for the full design (planned).
