import { z } from 'zod';
import { loadCatalog } from '../parsing/catalog.js';
import { resolveWindow } from '../util/time-windows.js';
import { scanPlayerLog } from '../sources/player-log.js';
import type { ParsedEvent } from '../parsing/types.js';
import type { ServerConfig } from '../config.js';

const AggregateKind = z.enum([
  'count',
  'group_by',
  'histogram',
  'distinct',
  'top',
]);

export const AggregateInput = z.object({
  source: z.enum(['player']).default('player'),
  agg: AggregateKind,
  field: z.string().optional(),
  bucket: z.enum(['1m', '5m', '15m', '1h', '1d']).optional(),
  top_n: z.number().int().min(1).max(1000).optional(),
  event_type: z.array(z.string()).optional(),
  since: z.string().optional(),
  until: z.string().optional(),
  between: z.tuple([z.string(), z.string()]).optional(),
  filter: z.record(z.string(), z.union([z.string(), z.number(), z.boolean()])).optional(),
});

export type AggregateArgs = z.infer<typeof AggregateInput>;

const DISTINCT_CAP = 100_000;

export async function runAggregate(args: AggregateArgs, config: ServerConfig) {
  const t0 = performance.now();
  const catalog = loadCatalog();
  const window = resolveWindow(new Date(), args);
  const eventTypeFilter = args.event_type ? new Set(args.event_type) : null;

  const stats = { scannedBytes: 0, scannedLines: 0 };
  let matched = 0;
  let truncated = false;
  let count = 0;
  const groups = new Map<string, number>();
  const distinct = new Set<string>();

  for await (const ev of scanPlayerLog(
    catalog,
    { path: config.playerLogPath, since: window.since, until: window.until },
    stats,
  )) {
    if (eventTypeFilter && !eventTypeFilter.has(ev.type)) continue;
    if (args.filter && !matchesFilter(ev, args.filter)) continue;
    matched += 1;

    switch (args.agg) {
      case 'count': count += 1; break;
      case 'group_by': {
        if (!args.field) throw new Error("'group_by' requires field");
        const k = String(extractAggField(ev, args.field));
        groups.set(k, (groups.get(k) ?? 0) + 1);
        break;
      }
      case 'histogram': {
        const bucket = args.bucket ?? '1h';
        const k = bucketize(ev.ts, bucket);
        groups.set(k, (groups.get(k) ?? 0) + 1);
        break;
      }
      case 'distinct': {
        if (!args.field) throw new Error("'distinct' requires field");
        if (distinct.size < DISTINCT_CAP) {
          distinct.add(String(extractAggField(ev, args.field)));
        } else {
          truncated = true;
        }
        break;
      }
      case 'top': {
        if (!args.field) throw new Error("'top' requires field");
        const k = String(extractAggField(ev, args.field));
        groups.set(k, (groups.get(k) ?? 0) + 1);
        break;
      }
    }
  }

  const elapsedMs = Math.round(performance.now() - t0);
  const summary = {
    matched,
    returned: 0,
    truncated,
    scannedBytes: stats.scannedBytes,
    scannedLines: stats.scannedLines,
    elapsedMs,
  };

  let buckets: Array<{ key: string; count: number }>;
  switch (args.agg) {
    case 'count':
      buckets = [{ key: 'count', count }];
      break;
    case 'distinct':
      buckets = Array.from(distinct, (v) => ({ key: v, count: 1 }));
      break;
    case 'top': {
      const n = args.top_n ?? 10;
      buckets = Array.from(groups, ([key, count]) => ({ key, count }))
        .sort((a, b) => b.count - a.count)
        .slice(0, n);
      break;
    }
    case 'group_by':
    case 'histogram':
      buckets = Array.from(groups, ([key, count]) => ({ key, count }))
        .sort((a, b) => (args.agg === 'histogram' ? a.key.localeCompare(b.key) : b.count - a.count));
      break;
  }

  summary.returned = buckets.length;
  return { summary, agg: args.agg, buckets };
}

function extractAggField(ev: ParsedEvent, field: string): unknown {
  // Support a few well-known top-level fields plus any data.* field.
  if (field === 'type') return ev.type;
  if (field === 'module') return ev.module;
  if (field === 'source') return ev.source;
  return ev.data[field] ?? '';
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

function bucketize(iso: string, bucket: string): string {
  const d = new Date(iso);
  const t = d.getTime();
  const sizes: Record<string, number> = {
    '1m': 60_000,
    '5m': 5 * 60_000,
    '15m': 15 * 60_000,
    '1h': 3_600_000,
    '1d': 86_400_000,
  };
  const size = sizes[bucket];
  if (!size) throw new Error(`Unknown bucket: ${bucket}`);
  const floored = Math.floor(t / size) * size;
  return new Date(floored).toISOString();
}
