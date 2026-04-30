import { z } from 'zod';
import { resolveAtAround, resolveWindow } from '../util/time-windows.js';
import {
  emptyMultiSourceRawStats,
  scanMultiSourceRaw,
  type RawSourceName,
} from '../sources/multi-source-raw.js';
import type { RawLineRecord } from '../sources/player-log-raw.js';
import { CursorStore } from '../state/cursors.js';
import {
  countAdvancedFiles,
  loadCursorsForName,
  persistCursor,
} from '../state/cursor-helpers.js';
import type { ServerConfig } from '../config.js';

/**
 * Raw-line counterpart to `query_events`. Streams Player.log + ChatLogs/
 * lines verbatim within a time window, applies a `[HH:MM:SS]` /
 * `YY-MM-DD HH:MM:SS\t` prefix gate to drop Unity boot noise + stack traces,
 * then filters survivors against an optional substring or regex pattern.
 *
 * Cursors live in the `raw-player` / `raw-chat` slots so a single named
 * cursor can hold both parsed-event positions (`query_events`) and raw
 * positions without one tool advancing past lines the other still wants.
 */

export const QueryRawLinesInput = z
  .object({
    source: z.enum(['player', 'chat', 'all']).default('player'),
    pattern: z.string().min(1).optional(),
    case_sensitive: z.boolean().optional().default(false),
    regex: z.boolean().optional().default(false),

    // Window form 1: since/until/between (same vocabulary as query_events).
    since: z.string().optional(),
    until: z.string().optional(),
    between: z.array(z.string()).length(2).optional(),

    // Window form 2: at/around (centered ± duration).
    at: z.string().optional(),
    around: z.string().optional(),

    cursor: z.string().optional(),
    context: z.number().int().min(0).max(50).optional().default(0),
    limit: z.number().int().min(1).max(10000).default(500),
    offset: z.number().int().min(0).default(0),
    fields: z.array(z.string()).optional(),
  })
  .refine(
    (v) => {
      const aroundForm = v.at !== undefined || v.around !== undefined;
      const sinceUntilForm = v.since !== undefined || v.until !== undefined;
      const betweenForm = v.between !== undefined;
      const formsUsed = [aroundForm, sinceUntilForm, betweenForm].filter(Boolean).length;
      // 0 forms = cursor-only or unbounded (handled downstream).
      // 1 form = legal.
      // 2+ forms = ambiguous; reject explicitly.
      return formsUsed <= 1;
    },
    {
      message:
        'Pick one time-window form: (since/until), between, or (at/around). Mixing them is ambiguous.',
    },
  )
  .refine(
    (v) => !(v.around !== undefined && v.at === undefined),
    { message: "'around' requires 'at' to anchor the window." },
  );

export type QueryRawLinesArgs = z.infer<typeof QueryRawLinesInput>;

const MAX_RESPONSE_BYTES = 5 * 1024 * 1024;

interface RawHit extends RawLineRecord {
  contextLines?: { before: string[]; after: string[] };
}

export async function runQueryRawLines(
  args: QueryRawLinesArgs,
  config: ServerConfig,
  cursorStore: CursorStore,
) {
  const t0 = performance.now();
  const now = new Date();

  let window;
  if (args.at !== undefined) {
    window = resolveAtAround(now, args.at, args.around);
  } else if (args.cursor && !args.since && !args.until && !args.between) {
    // Cursor-only query: don't bound by time, resume through "now".
    window = { since: new Date(0), until: now };
  } else {
    window = resolveWindow(now, args);
  }

  const cursorsByName = loadCursorsForName(cursorStore, args.cursor);
  const stats = emptyMultiSourceRawStats();

  const matcher = buildMatcher(args);

  const ringBefore: string[] = [];
  const N = args.context ?? 0;
  const pending: Array<{ hit: RawHit; after: string[] }> = [];

  let matched = 0;
  let skipped = 0;
  let truncated = false;
  let bytesEmitted = 0;
  const out: RawHit[] = [];

  const queryShape = {
    source: args.source as RawSourceName,
    since: window.since,
    until: window.until,
    cursors: cursorsByName
      ? {
          'raw-player': cursorsByName['raw-player'],
          'raw-chat': cursorsByName['raw-chat'],
        }
      : undefined,
  };

  for await (const rec of scanMultiSourceRaw(config, queryShape, stats)) {
    // Feed line as after-context to any pending hits; emit once their window fills.
    if (N > 0) {
      for (const p of pending) {
        if (p.after.length < N) p.after.push(rec.raw);
      }
      while (pending.length > 0 && pending[0]!.after.length >= N) {
        const h = pending.shift()!.hit;
        if (!emit(h)) break;
      }
    }

    if (!matcher(rec.raw)) {
      if (N > 0) pushRing(ringBefore, rec.raw, N);
      continue;
    }

    matched += 1;
    if (skipped < args.offset) {
      skipped += 1;
      if (N > 0) pushRing(ringBefore, rec.raw, N);
      continue;
    }
    if (out.length >= args.limit) {
      truncated = true;
      if (N > 0) pushRing(ringBefore, rec.raw, N);
      continue;
    }

    const hit: RawHit = { ...rec };
    if (N > 0) {
      hit.contextLines = { before: [...ringBefore], after: [] };
      pending.push({ hit, after: hit.contextLines.after });
    } else {
      if (!emit(hit)) break;
    }

    if (N > 0) pushRing(ringBefore, rec.raw, N);
  }

  // Drain any pending hits whose after-windows didn't fill before EOF.
  while (pending.length > 0) {
    const h = pending.shift()!.hit;
    if (!emit(h)) break;
  }

  if (args.cursor) {
    persistCursor(cursorStore, args.cursor, stats);
  }

  const elapsedMs = Math.round(performance.now() - t0);
  return {
    summary: {
      matched,
      returned: out.length,
      truncated,
      scannedBytes: stats.scannedBytes,
      scannedLines: stats.scannedLines,
      droppedNonGameLines: stats.droppedNonGameLines,
      elapsedMs,
      window: { since: window.since.toISOString(), until: window.until.toISOString() },
      cursor: args.cursor
        ? {
            name: args.cursor,
            advanced: countAdvancedFiles(stats),
            rolledOverFiles: stats.rolledOverFiles,
          }
        : undefined,
    },
    lines: out.map((h) => projectFields(h, args.fields)),
  };

  function emit(h: RawHit): boolean {
    const projected = projectFields(h, args.fields);
    const encoded = JSON.stringify(projected);
    if (bytesEmitted + encoded.length > MAX_RESPONSE_BYTES) {
      truncated = true;
      return false;
    }
    out.push(h);
    bytesEmitted += encoded.length + 1;
    return true;
  }
}

function buildMatcher(args: QueryRawLinesArgs): (line: string) => boolean {
  if (!args.pattern) return () => true;
  if (args.regex) {
    const flags = args.case_sensitive ? '' : 'i';
    const re = new RegExp(args.pattern, flags);
    return (line) => re.test(line);
  }
  if (args.case_sensitive) {
    const needle = args.pattern;
    return (line) => line.includes(needle);
  }
  const needle = args.pattern.toLowerCase();
  return (line) => line.toLowerCase().includes(needle);
}

function pushRing(ring: string[], line: string, max: number): void {
  if (max <= 0) return;
  ring.push(line);
  while (ring.length > max) ring.shift();
}

function projectFields(
  hit: RawHit,
  fields: string[] | undefined,
): Record<string, unknown> {
  const full: Record<string, unknown> = {
    ts: hit.ts.toISOString(),
    source: hit.source,
    file: hit.file,
    line: hit.line,
    raw: hit.raw,
  };
  if (hit.contextLines) full.contextLines = hit.contextLines;
  if (!fields || fields.length === 0) return full;
  const out: Record<string, unknown> = {};
  for (const f of fields) {
    if (f in full) out[f] = full[f];
  }
  return out;
}
