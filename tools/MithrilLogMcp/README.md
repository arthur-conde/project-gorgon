# MithrilLogMcp

MCP server exposing Mithril's log sources (Project Gorgon `Player.log`, `ChatLogs/`, and Mithril's own Serilog output) to Claude Code with parsed/typed events, time-windowed queries, and server-side aggregations. Designed to keep the cost of AI-assisted log analysis bounded — instead of having Claude `grep` and `Read` hundreds of MB, the server returns structured events and summaries.

## Status

- **v0.1** — Player.log only, `query_events`/`aggregate`/`list_event_types`, time windows.
- **v0.2** — adds chat log + Mithril Serilog sources, `source: "all"` merge, field projection.
- **v0.3** — persistent cursors, character scoping, `--last-session`, rollover detection.

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
