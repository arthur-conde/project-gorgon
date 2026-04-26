import { z } from 'zod';
import { loadCatalog } from '../parsing/catalog.js';
import { resolveWindow } from '../util/time-windows.js';
import { scanMultiSource, type SourceName } from '../sources/multi-source.js';
import { resolveLastSessionWindow } from '../state/character-resolver.js';
import type { ParsedEvent } from '../parsing/types.js';
import type { ServerConfig } from '../config.js';

export const QueryEventsInput = z.object({
  source: z.enum(['player', 'chat', 'mithril', 'all']).default('player'),
  event_type: z.array(z.string()).optional(),
  since: z.string().optional(),
  until: z.string().optional(),
  between: z.tuple([z.string(), z.string()]).optional(),
  last_session: z.boolean().optional(),
  character: z.string().optional(),
  filter: z.record(z.string(), z.union([z.string(), z.number(), z.boolean()])).optional(),
  fields: z.array(z.string()).optional(),
  context: z.number().int().min(0).max(50).optional(),
  limit: z.number().int().min(1).max(10000).default(500),
  offset: z.number().int().min(0).default(0),
  format: z.enum(['ndjson', 'json']).default('ndjson'),
});

export type QueryEventsArgs = z.infer<typeof QueryEventsInput>;

const MAX_RESPONSE_BYTES = 5 * 1024 * 1024;

export async function runQueryEvents(args: QueryEventsArgs, config: ServerConfig) {
  const t0 = performance.now();
  const catalog = loadCatalog();
  const now = new Date();

  let window;
  if (args.last_session) {
    if (!args.character) {
      throw new Error("'last_session' requires 'character'");
    }
    window = resolveLastSessionWindow(config, catalog, args.character, now);
  } else {
    window = resolveWindow(now, args);
  }

  const stats = { scannedBytes: 0, scannedLines: 0 };
  let matched = 0;
  let skipped = 0;
  let truncated = false;
  let bytesEmitted = 0;
  const events: ParsedEvent[] = [];
  const eventTypeFilter = args.event_type ? new Set(args.event_type) : null;
  const characterFilter = args.character;

  for await (const ev of scanMultiSource(
    catalog,
    config,
    { source: args.source as SourceName, since: window.since, until: window.until },
    stats,
  )) {
    if (eventTypeFilter && !eventTypeFilter.has(ev.type)) continue;
    if (args.filter && !matchesFilter(ev, args.filter)) continue;
    if (characterFilter && !matchesCharacter(ev, characterFilter)) continue;

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
    bytesEmitted += encoded.length + 1;
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
      window: { since: window.since.toISOString(), until: window.until.toISOString() },
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
    if (typeof expected === 'string') {
      if (String(actual) !== expected) return false;
    } else if (actual !== expected) {
      return false;
    }
  }
  return true;
}

/**
 * Best-effort character match. Chat events surface the character name in
 * `data.speaker`; player events themselves don't carry the active character
 * (it's tracked across-the-stream by the .NET-side ActiveCharacterLogSynchronizer).
 * For v0.2 we match on speaker for chat and accept all player events when a
 * character filter is set — the proper cross-stream stamping lives in v0.3
 * once cursors land and we can keep an active-character running state.
 */
function matchesCharacter(ev: ParsedEvent, character: string): boolean {
  if (ev.source === 'chat') {
    const speaker = ev.data.speaker;
    return typeof speaker === 'string' && speaker === character;
  }
  if (ev.type === 'shared.ProcessAddPlayer') {
    return ev.data.characterName === character;
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
