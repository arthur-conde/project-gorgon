#!/usr/bin/env node
import { Server } from '@modelcontextprotocol/sdk/server/index.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from '@modelcontextprotocol/sdk/types.js';
import { z } from 'zod';
import { zodToJsonSchema as zodToJsonSchemaLib } from 'zod-to-json-schema';

import { loadConfig } from './config.js';
import {
  AggregateInput,
  runAggregate,
} from './tools/aggregate.js';
import {
  ListEventTypesInput,
  runListEventTypes,
} from './tools/list-event-types.js';
import {
  QueryEventsInput,
  runQueryEvents,
} from './tools/query-events.js';
import {
  CursorListInput,
  CursorResetInput,
  CursorSetInput,
  runCursorList,
  runCursorReset,
  runCursorSet,
} from './tools/cursor.js';
import { ServerInfoInput, runServerInfo } from './tools/server-info.js';
import {
  QueryRawLinesInput,
  runQueryRawLines,
} from './tools/query-raw-lines.js';
import { CursorStore } from './state/cursors.js';

/**
 * Entrypoint. Wires the three v0.1 tools to the MCP stdio transport.
 *
 * Diagnostics go to stderr so they don't corrupt the MCP JSON-RPC framing on
 * stdout. Tool handlers are wrapped in try/catch so an exception becomes
 * `{ isError: true }` rather than a transport crash.
 */

const SERVER_NAME = 'mithril-log-mcp';
const SERVER_VERSION = '0.2.0';

const TOOLS = [
  {
    name: 'server_info',
    description:
      'Returns the mithril-log-mcp version, the on-disk mtime of the running ' +
      'JS bundle (the "when was this last built" signal — useful since the ' +
      'server is rebuilt locally rather than released), and the resolved log ' +
      'paths the server reads from.',
    inputSchema: zodToJsonSchema(ServerInfoInput),
  },
  {
    name: 'query_events',
    description:
      'Stream typed events from Mithril log sources within a time window. ' +
      'Sources: player (Player.log), chat (ChatLogs/), mithril (Serilog), all (merged by ts). ' +
      'Pass character + last_session=true for "everything from this character\'s most recent session".',
    inputSchema: zodToJsonSchema(QueryEventsInput),
  },
  {
    name: 'aggregate',
    description:
      'Server-side aggregations (count, group_by, histogram, distinct, top) ' +
      'over the same event stream. Returns a small bucket list instead of ' +
      'megabytes of raw events.',
    inputSchema: zodToJsonSchema(AggregateInput),
  },
  {
    name: 'query_raw_lines',
    description:
      'Stream raw log lines from Player.log + ChatLogs/ verbatim, dropping ' +
      'Unity boot output / stack traces (everything without a [HH:MM:SS] or ' +
      'YY-MM-DD HH:MM:SS prefix). Optional substring or regex filter. ' +
      'Time-window forms (pick one): since/until, between, or at/around ' +
      '("at: <iso-or-2h>, around: 30s" → ±30s window). Cursors live in ' +
      'separate raw-player / raw-chat slots so they do not collide with ' +
      'query_events cursors of the same name.',
    inputSchema: zodToJsonSchema(QueryRawLinesInput),
  },
  {
    name: 'list_event_types',
    description:
      'Lists every event type the server can emit, with field names and types. ' +
      'Use this to discover what queries are possible.',
    inputSchema: zodToJsonSchema(ListEventTypesInput),
  },
  {
    name: 'cursor_list',
    description:
      'Lists named cursors stored at %LocalAppData%/MithrilLogMcp/cursors.json. ' +
      'Cursors track per-file byte offsets so subsequent queries can resume.',
    inputSchema: zodToJsonSchema(CursorListInput),
  },
  {
    name: 'cursor_reset',
    description:
      'Removes a named cursor so the next query starts from the configured ' +
      'time window again.',
    inputSchema: zodToJsonSchema(CursorResetInput),
  },
  {
    name: 'cursor_set',
    description:
      'Manually positions a named cursor for one source/file. anchor=start ' +
      're-reads from byte 0; anchor=end fast-forwards to the current end of ' +
      "file (skip everything written before now); anchor=offset uses an exact " +
      'byteOffset.',
    inputSchema: zodToJsonSchema(CursorSetInput),
  },
] as const;

async function main(): Promise<void> {
  const config = loadConfig();
  const cursorStore = new CursorStore();
  const server = new Server(
    { name: SERVER_NAME, version: SERVER_VERSION },
    { capabilities: { tools: {} } },
  );

  server.setRequestHandler(ListToolsRequestSchema, async () => ({ tools: [...TOOLS] }));

  server.setRequestHandler(CallToolRequestSchema, async (req) => {
    const { name, arguments: rawArgs } = req.params;
    try {
      switch (name) {
        case 'server_info': {
          const args = ServerInfoInput.parse(rawArgs ?? {});
          const result = runServerInfo(
            args,
            { name: SERVER_NAME, version: SERVER_VERSION },
            config,
          );
          return {
            content: [{ type: 'text', text: JSON.stringify(result, null, 2) }],
          };
        }
        case 'query_events': {
          const args = QueryEventsInput.parse(rawArgs ?? {});
          const result = await runQueryEvents(args, config, cursorStore);
          return {
            content: [
              { type: 'text', text: JSON.stringify(result.summary) },
              { type: 'text', text: encodeEvents(result.events, args.format) },
            ],
          };
        }
        case 'aggregate': {
          const args = AggregateInput.parse(rawArgs ?? {});
          const result = await runAggregate(args, config, cursorStore);
          return {
            content: [{ type: 'text', text: JSON.stringify(result, null, 2) }],
          };
        }
        case 'query_raw_lines': {
          const args = QueryRawLinesInput.parse(rawArgs ?? {});
          const result = await runQueryRawLines(args, config, cursorStore);
          return {
            content: [{ type: 'text', text: JSON.stringify(result, null, 2) }],
          };
        }
        case 'list_event_types': {
          const args = ListEventTypesInput.parse(rawArgs ?? {});
          const result = runListEventTypes(args);
          return {
            content: [{ type: 'text', text: JSON.stringify(result, null, 2) }],
          };
        }
        case 'cursor_list': {
          const args = CursorListInput.parse(rawArgs ?? {});
          const result = runCursorList(args, cursorStore);
          return {
            content: [{ type: 'text', text: JSON.stringify(result, null, 2) }],
          };
        }
        case 'cursor_reset': {
          const args = CursorResetInput.parse(rawArgs ?? {});
          const result = runCursorReset(args, cursorStore);
          return {
            content: [{ type: 'text', text: JSON.stringify(result, null, 2) }],
          };
        }
        case 'cursor_set': {
          const args = CursorSetInput.parse(rawArgs ?? {});
          const result = runCursorSet(args, cursorStore);
          return {
            content: [{ type: 'text', text: JSON.stringify(result, null, 2) }],
          };
        }
        default:
          return errorResult(`Unknown tool: ${name}`);
      }
    } catch (e) {
      const message = e instanceof Error ? e.message : String(e);
      const where = e instanceof Error && e.stack ? `\n${e.stack}` : '';
      return errorResult(`${name} failed: ${message}${where}`);
    }
  });

  const transport = new StdioServerTransport();
  await server.connect(transport);
  console.error(`[${SERVER_NAME}] connected via stdio`);
}

function encodeEvents(events: object[], format: 'ndjson' | 'json'): string {
  if (format === 'json') return JSON.stringify(events);
  return events.map((e) => JSON.stringify(e)).join('\n');
}

function errorResult(message: string) {
  return { isError: true, content: [{ type: 'text', text: message }] };
}

/**
 * Zod -> JSON Schema bridge. Delegates to `zod-to-json-schema` so every Zod
 * construct (refine/effects, literal, nullable, discriminatedUnion, …) maps
 * to a strictly-valid JSON Schema. `target: 'jsonSchema7'` matches what MCP
 * clients (Claude Code, Continue.dev, etc.) expect; `$refStrategy: 'none'`
 * inlines everything so a single tool's inputSchema is self-contained.
 */
function zodToJsonSchema(schema: z.ZodType): Record<string, unknown> {
  const out = zodToJsonSchemaLib(schema, {
    target: 'jsonSchema7',
    $refStrategy: 'none',
  }) as Record<string, unknown>;
  // Strip the top-level $schema marker — MCP clients don't need it and some
  // strict validators reject unknown keys at the inputSchema root.
  delete out.$schema;
  return out;
}

main().catch((err) => {
  console.error(`[${SERVER_NAME}] fatal:`, err);
  process.exit(1);
});
