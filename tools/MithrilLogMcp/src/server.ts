#!/usr/bin/env node
import { Server } from '@modelcontextprotocol/sdk/server/index.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from '@modelcontextprotocol/sdk/types.js';
import { z } from 'zod';

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
import { CursorStore } from './state/cursors.js';

/**
 * Entrypoint. Wires the three v0.1 tools to the MCP stdio transport.
 *
 * Diagnostics go to stderr so they don't corrupt the MCP JSON-RPC framing on
 * stdout. Tool handlers are wrapped in try/catch so an exception becomes
 * `{ isError: true }` rather than a transport crash.
 */

const SERVER_NAME = 'mithril-log-mcp';
const SERVER_VERSION = '0.1.0';

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
 * Minimal Zod -> JSON Schema bridge — the MCP SDK accepts plain JSON Schema
 * for `inputSchema`. A full library (zod-to-json-schema) would be more
 * accurate, but this server's schemas are simple enough that the Zod parser
 * does the real validation at call time and the JSON Schema is just a
 * description for the client.
 */
function zodToJsonSchema(schema: z.ZodType): Record<string, unknown> {
  return describeSchema(schema);
}

function describeSchema(schema: z.ZodType): Record<string, unknown> {
  const def = (schema as any)._def;
  if (!def) return { type: 'object' };

  switch (def.typeName) {
    case 'ZodObject': {
      const shape = def.shape();
      const properties: Record<string, unknown> = {};
      const required: string[] = [];
      for (const [k, v] of Object.entries(shape)) {
        const child = v as z.ZodType;
        properties[k] = describeSchema(child);
        if (!isOptional(child)) required.push(k);
      }
      return { type: 'object', properties, ...(required.length ? { required } : {}) };
    }
    case 'ZodArray':
      return { type: 'array', items: describeSchema(def.type) };
    case 'ZodTuple':
      return { type: 'array', prefixItems: def.items.map((i: z.ZodType) => describeSchema(i)) };
    case 'ZodEnum':
      return { type: 'string', enum: def.values };
    case 'ZodString':
      return { type: 'string' };
    case 'ZodNumber':
      return { type: 'number' };
    case 'ZodBoolean':
      return { type: 'boolean' };
    case 'ZodOptional':
    case 'ZodDefault':
      return describeSchema(def.innerType);
    case 'ZodUnion':
      return { anyOf: def.options.map((o: z.ZodType) => describeSchema(o)) };
    case 'ZodRecord':
      return { type: 'object', additionalProperties: describeSchema(def.valueType) };
    default:
      return {};
  }
}

function isOptional(schema: z.ZodType): boolean {
  const def = (schema as any)._def;
  return def?.typeName === 'ZodOptional' || def?.typeName === 'ZodDefault';
}

main().catch((err) => {
  console.error(`[${SERVER_NAME}] fatal:`, err);
  process.exit(1);
});
