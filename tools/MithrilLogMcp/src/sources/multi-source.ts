import { scanPlayerLog, type PlayerLogScanStats } from './player-log.js';
import { scanChatLogs, type ChatScanStats } from './chat-log.js';
import { scanMithrilSerilog, type MithrilSerilogScanStats } from './mithril-serilog.js';
import type { Catalog } from '../parsing/catalog.js';
import type { ParsedEvent } from '../parsing/types.js';
import type { ServerConfig } from '../config.js';
import type { CursorState, FileCursor } from '../state/cursors.js';

export type SourceName = 'player' | 'chat' | 'mithril' | 'all';

export interface MultiSourceStats {
  scannedBytes: number;
  scannedLines: number;
  /**
   * Where each file was last read up to. Aggregated across sources for the
   * `all` case. Callers use this to advance cursors after a successful query.
   */
  endOffsetsBySource: Record<string, Record<string, number>>;
  rolledOverFiles: string[];
}

export interface MultiSourceQuery {
  source: SourceName;
  since: Date;
  until: Date;
  /** Optional per-source cursor state for resumed reads. */
  cursors?: Record<string, CursorState> | undefined;
}

const PER_SOURCE_KEYS: Array<Exclude<SourceName, 'all'>> = ['player', 'chat', 'mithril'];

export async function* scanMultiSource(
  catalog: Catalog,
  config: ServerConfig,
  query: MultiSourceQuery,
  stats: MultiSourceStats,
): AsyncGenerator<ParsedEvent, void, void> {
  // Pre-create slots for every requested source so callers can safely read
  // `endOffsetsBySource[s]` even when nothing was scanned (e.g. a missing
  // chat dir on a fresh install).
  const sourcesToScan = query.source === 'all' ? PER_SOURCE_KEYS : [query.source];
  for (const s of sourcesToScan) stats.endOffsetsBySource[s] = {};

  if (query.source !== 'all') {
    yield* singleSource(catalog, config, query, stats);
    return;
  }

  const playerStats = makeSubStats();
  const chatStats = makeSubStats();
  const serilogStats = makeSubStats();

  const iterators: AsyncIterator<ParsedEvent>[] = [
    scanPlayerLog(catalog, {
      path: config.playerLogPath,
      since: query.since,
      until: query.until,
      prevCursor: cursorEntryFor(query.cursors?.player, config.playerLogPath),
    }, playerStats)[Symbol.asyncIterator](),
    scanChatLogs(catalog, {
      dir: config.chatLogDir,
      since: query.since,
      until: query.until,
      prevCursors: query.cursors?.chat?.perFile,
    }, chatStats)[Symbol.asyncIterator](),
    scanMithrilSerilog({
      dir: config.mithrilLogDir,
      since: query.since,
      until: query.until,
      prevCursors: query.cursors?.mithril?.perFile,
    }, serilogStats)[Symbol.asyncIterator](),
  ];

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
  stats.endOffsetsBySource.player = playerStats.endOffsets;
  stats.endOffsetsBySource.chat = chatStats.endOffsets;
  stats.endOffsetsBySource.mithril = serilogStats.endOffsets;
  stats.rolledOverFiles.push(
    ...playerStats.rolledOverFiles,
    ...chatStats.rolledOverFiles,
    ...serilogStats.rolledOverFiles,
  );
}

async function* singleSource(
  catalog: Catalog,
  config: ServerConfig,
  query: MultiSourceQuery,
  stats: MultiSourceStats,
): AsyncGenerator<ParsedEvent, void, void> {
  switch (query.source) {
    case 'player': {
      const sub = makeSubStats();
      yield* scanPlayerLog(catalog, {
        path: config.playerLogPath,
        since: query.since,
        until: query.until,
        prevCursor: cursorEntryFor(query.cursors?.player, config.playerLogPath),
      }, sub);
      stats.scannedBytes = sub.scannedBytes;
      stats.scannedLines = sub.scannedLines;
      stats.endOffsetsBySource.player = sub.endOffsets;
      stats.rolledOverFiles.push(...sub.rolledOverFiles);
      return;
    }
    case 'chat': {
      const sub = makeSubStats();
      yield* scanChatLogs(catalog, {
        dir: config.chatLogDir,
        since: query.since,
        until: query.until,
        prevCursors: query.cursors?.chat?.perFile,
      }, sub);
      stats.scannedBytes = sub.scannedBytes;
      stats.scannedLines = sub.scannedLines;
      stats.endOffsetsBySource.chat = sub.endOffsets;
      stats.rolledOverFiles.push(...sub.rolledOverFiles);
      return;
    }
    case 'mithril': {
      const sub = makeSubStats();
      yield* scanMithrilSerilog({
        dir: config.mithrilLogDir,
        since: query.since,
        until: query.until,
        prevCursors: query.cursors?.mithril?.perFile,
      }, sub);
      stats.scannedBytes = sub.scannedBytes;
      stats.scannedLines = sub.scannedLines;
      stats.endOffsetsBySource.mithril = sub.endOffsets;
      stats.rolledOverFiles.push(...sub.rolledOverFiles);
      return;
    }
    default:
      throw new Error(`Unknown source: ${query.source}`);
  }
}

function makeSubStats(): PlayerLogScanStats & ChatScanStats & MithrilSerilogScanStats {
  return { scannedBytes: 0, scannedLines: 0, endOffsets: {}, rolledOverFiles: [] };
}

function cursorEntryFor(state: CursorState | undefined, file: string): FileCursor | undefined {
  return state?.perFile[file];
}

export function emptyMultiSourceStats(): MultiSourceStats {
  return {
    scannedBytes: 0,
    scannedLines: 0,
    endOffsetsBySource: {},
    rolledOverFiles: [],
  };
}
