# MithrilLogMcp

MCP server exposing Mithril's log sources (Project Gorgon `Player.log`, `ChatLogs/`, and Mithril's own Serilog output) to Claude Code with parsed/typed events, time-windowed queries, and server-side aggregations. Designed to keep the cost of AI-assisted log analysis bounded — instead of having Claude `grep` and `Read` hundreds of MB, the server returns structured events and summaries.

## Status

- **v0.1** — Player.log only, `query_events`/`aggregate`/`list_event_types`, time windows. ✅
- **v0.2** — chat log + Mithril Serilog sources, `source: "all"` merge, field projection. ✅
- **v0.3** — persistent cursors, character scoping, `--last-session`, rollover detection. ✅

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
