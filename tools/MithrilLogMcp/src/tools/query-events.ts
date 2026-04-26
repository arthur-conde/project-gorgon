import { z } from 'zod';
import { loadCatalog } from '../parsing/catalog.js';
import { resolveWindow } from '../util/time-windows.js';
import { scanPlayerLog } from '../sources/player-log.js';
import type { ParsedEvent } from '../parsing/types.js';
import type { ServerConfig } from '../config.js';

export const QueryEventsInput = z.object({
  source: z.enum(['player']).default('player'),
  event_type: z.array(z.string()).optional(),
  since: z.string().optional(),
  until: z.string().optional(),
  between: z.tuple([z.string(), z.string()]).optional(),
  filter: z.record(z.string(), z.union([z.string(), z.number(), z.boolean()])).optional(),
  fields: z.array(z.string()).optional(),
  limit: z.number().int().min(1).max(10000).default(500),
  offset: z.number().int().min(0).default(0),
  format: z.enum(['ndjson', 'json']).default('ndjson'),
});

export type QueryEventsArgs = z.infer<typeof QueryEventsInput>;

const MAX_RESPONSE_BYTES = 5 * 1024 * 1024;

export async function runQueryEvents(args: QueryEventsArgs, config: ServerConfig) {
  const t0 = performance.now();
  const catalog = loadCatalog();
  const window = resolveWindow(new Date(), args);

  const stats = { scannedBytes: 0, scannedLines: 0 };
  let matched = 0;
  let skipped = 0;
  let truncated = false;
  let bytesEmitted = 0;
  const events: ParsedEvent[] = [];
  const eventTypeFilter = args.event_type ? new Set(args.event_type) : null;

  for await (const ev of scanPlayerLog(
    catalog,
    { path: config.playerLogPath, since: window.since, until: window.until },
    stats,
  )) {
    if (eventTypeFilter && !eventTypeFilter.has(ev.type)) continue;
    if (args.filter && !matchesFilter(ev, args.filter)) continue;

    matched += 1;
    if (skipped < args.offset) {
      skipped += 1;
      continue;
    }

    if (events.length >= args.limit) {
      truncated = true;
      continue;
    }

    const projected = args.fields ? projectFields(ev, args.fields) : ev;
    const encoded = JSON.stringify(projected);
    if (bytesEmitted + encoded.length > MAX_RESPONSE_BYTES) {
      truncated = true;
      break;
    }
    events.push(projected);
    bytesEmitted += encoded.length + 1; // + newline
  }

  const elapsedMs = Math.round(performance.now() - t0);

  return {
    summary: {
      matched,
      returned: events.length,
      truncated,
      scannedBytes: stats.scannedBytes,
      scannedLines: stats.scannedLines,
      elapsedMs,
    },
    events,
    format: args.format,
  };
}

function matchesFilter(
  ev: ParsedEvent,
  filter: Record<string, string | number | boolean>,
): boolean {
  for (const [k, expected] of Object.entries(filter)) {
    const actual = ev.data[k];
    if (actual === undefined) return false;
    // String filters are exact-equal; everything else uses ==.
    if (typeof expected === 'string') {
      if (String(actual) !== expected) return false;
    } else if (actual !== expected) {
      return false;
    }
  }
  return true;
}

function projectFields(ev: ParsedEvent, fields: string[]): ParsedEvent {
  const data: Record<string, unknown> = {};
  for (const f of fields) {
    if (f in ev.data) data[f] = ev.data[f];
  }
  return { ...ev, data };
}
