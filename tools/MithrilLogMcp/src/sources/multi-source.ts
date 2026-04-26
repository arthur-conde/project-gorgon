import { scanPlayerLog, type PlayerLogScanStats } from './player-log.js';
import { scanChatLogs, type ChatScanStats } from './chat-log.js';
import { scanMithrilSerilog, type MithrilSerilogScanStats } from './mithril-serilog.js';
import type { Catalog } from '../parsing/catalog.js';
import type { ParsedEvent } from '../parsing/types.js';
import type { ServerConfig } from '../config.js';

export type SourceName = 'player' | 'chat' | 'mithril' | 'all';

export interface MultiSourceStats {
  scannedBytes: number;
  scannedLines: number;
}

export interface MultiSourceQuery {
  source: SourceName;
  since: Date;
  until: Date;
}

/**
 * Yields parsed events from one or more sources, merged in timestamp order.
 *
 * For a single source, falls through to the underlying scanner. For
 * `source: "all"`, opens one async iterator per known source and yields
 * the lowest-timestamp event across all of them on each step (manual
 * 3-way heap; small enough that an actual heap would just add overhead).
 */
export async function* scanMultiSource(
  catalog: Catalog,
  config: ServerConfig,
  query: MultiSourceQuery,
  stats: MultiSourceStats,
): AsyncGenerator<ParsedEvent, void, void> {
  if (query.source !== 'all') {
    yield* singleSource(catalog, config, query, stats);
    return;
  }

  const playerStats: PlayerLogScanStats = { scannedBytes: 0, scannedLines: 0 };
  const chatStats: ChatScanStats = { scannedBytes: 0, scannedLines: 0 };
  const serilogStats: MithrilSerilogScanStats = { scannedBytes: 0, scannedLines: 0 };

  const iterators: AsyncIterator<ParsedEvent>[] = [
    scanPlayerLog(catalog, { path: config.playerLogPath, since: query.since, until: query.until }, playerStats)[Symbol.asyncIterator](),
    scanChatLogs(catalog, { dir: config.chatLogDir, since: query.since, until: query.until }, chatStats)[Symbol.asyncIterator](),
    scanMithrilSerilog({ dir: config.mithrilLogDir, since: query.since, until: query.until }, serilogStats)[Symbol.asyncIterator](),
  ];

  // Buffer one event per iterator. Pop the smallest, advance that iterator,
  // refill its slot. End when every slot is empty.
  const slots: Array<{ ev: ParsedEvent; iter: AsyncIterator<ParsedEvent> } | null> = [];
  for (const it of iterators) {
    const next = await it.next();
    slots.push(next.done ? null : { ev: next.value, iter: it });
  }

  while (slots.some((s) => s !== null)) {
    let bestIdx = -1;
    let bestTs = Number.POSITIVE_INFINITY;
    for (let i = 0; i < slots.length; i++) {
      const s = slots[i];
      if (!s) continue;
      const t = Date.parse(s.ev.ts);
      if (t < bestTs) {
        bestTs = t;
        bestIdx = i;
      }
    }
    if (bestIdx < 0) break;
    const winner = slots[bestIdx]!;
    yield winner.ev;
    const next = await winner.iter.next();
    slots[bestIdx] = next.done ? null : { ev: next.value, iter: winner.iter };
  }

  stats.scannedBytes = playerStats.scannedBytes + chatStats.scannedBytes + serilogStats.scannedBytes;
  stats.scannedLines = playerStats.scannedLines + chatStats.scannedLines + serilogStats.scannedLines;
}

async function* singleSource(
  catalog: Catalog,
  config: ServerConfig,
  query: MultiSourceQuery,
  stats: MultiSourceStats,
): AsyncGenerator<ParsedEvent, void, void> {
  switch (query.source) {
    case 'player': {
      const s: PlayerLogScanStats = { scannedBytes: 0, scannedLines: 0 };
      yield* scanPlayerLog(catalog, { path: config.playerLogPath, since: query.since, until: query.until }, s);
      stats.scannedBytes = s.scannedBytes;
      stats.scannedLines = s.scannedLines;
      return;
    }
    case 'chat': {
      const s: ChatScanStats = { scannedBytes: 0, scannedLines: 0 };
      yield* scanChatLogs(catalog, { dir: config.chatLogDir, since: query.since, until: query.until }, s);
      stats.scannedBytes = s.scannedBytes;
      stats.scannedLines = s.scannedLines;
      return;
    }
    case 'mithril': {
      const s: MithrilSerilogScanStats = { scannedBytes: 0, scannedLines: 0 };
      yield* scanMithrilSerilog({ dir: config.mithrilLogDir, since: query.since, until: query.until }, s);
      stats.scannedBytes = s.scannedBytes;
      stats.scannedLines = s.scannedLines;
      return;
    }
    default:
      throw new Error(`Unknown source: ${query.source}`);
  }
}
