import { scanPlayerLogRaw, type PlayerLogRawScanStats, type RawLineRecord } from './player-log-raw.js';
import { scanChatLogsRaw, type ChatLogRawScanStats } from './chat-log-raw.js';
import type { ServerConfig } from '../config.js';
import type { CursorState } from '../state/cursors.js';

/**
 * Orchestrates the raw-line scanners across one or more sources, mirroring
 * `multi-source.ts` for parsed events. Mithril Serilog is excluded — it's
 * CompactJsonFormatter, not raw text, and is already exposed through
 * `query_events` as `mithril.*` event types.
 */

export type RawSourceName = 'player' | 'chat' | 'all';

export interface MultiSourceRawStats {
  scannedBytes: number;
  scannedLines: number;
  droppedNonGameLines: number;
  /**
   * Per-source end byte offsets keyed by absolute file path. Persisted as
   * cursors under the `raw-player` / `raw-chat` slot names so they don't
   * collide with `query_events` cursors.
   */
  endOffsetsBySource: Record<string, Record<string, number>>;
  rolledOverFiles: string[];
}

export interface MultiSourceRawQuery {
  source: RawSourceName;
  since: Date;
  until: Date;
  cursors?: { 'raw-player'?: CursorState | undefined; 'raw-chat'?: CursorState | undefined } | undefined;
}

const PER_SOURCE_KEYS: Array<Exclude<RawSourceName, 'all'>> = ['player', 'chat'];

export async function* scanMultiSourceRaw(
  config: ServerConfig,
  query: MultiSourceRawQuery,
  stats: MultiSourceRawStats,
): AsyncGenerator<RawLineRecord, void, void> {
  const sourcesToScan = query.source === 'all' ? PER_SOURCE_KEYS : [query.source];
  for (const s of sourcesToScan) stats.endOffsetsBySource[`raw-${s}`] = {};

  if (query.source !== 'all') {
    yield* singleSource(config, query, stats);
    return;
  }

  const playerStats = makeSubStats();
  const chatStats = makeSubStats();

  const iterators: AsyncIterator<RawLineRecord>[] = [
    scanPlayerLogRaw({
      path: config.playerLogPath,
      since: query.since,
      until: query.until,
      prevCursor: query.cursors?.['raw-player']?.perFile?.[config.playerLogPath],
    }, playerStats)[Symbol.asyncIterator](),
    scanChatLogsRaw({
      dir: config.chatLogDir,
      since: query.since,
      until: query.until,
      prevCursors: query.cursors?.['raw-chat']?.perFile,
    }, chatStats)[Symbol.asyncIterator](),
  ];

  const slots: Array<{ rec: RawLineRecord; iter: AsyncIterator<RawLineRecord> } | null> = [];
  for (const it of iterators) {
    const next = await it.next();
    slots.push(next.done ? null : { rec: next.value, iter: it });
  }

  while (slots.some((s) => s !== null)) {
    let bestIdx = -1;
    let bestTs = Number.POSITIVE_INFINITY;
    for (let i = 0; i < slots.length; i++) {
      const s = slots[i];
      if (!s) continue;
      const t = s.rec.ts.getTime();
      if (t < bestTs) {
        bestTs = t;
        bestIdx = i;
      }
    }
    if (bestIdx < 0) break;
    const winner = slots[bestIdx]!;
    yield winner.rec;
    const next = await winner.iter.next();
    slots[bestIdx] = next.done ? null : { rec: next.value, iter: winner.iter };
  }

  stats.scannedBytes = playerStats.scannedBytes + chatStats.scannedBytes;
  stats.scannedLines = playerStats.scannedLines + chatStats.scannedLines;
  stats.droppedNonGameLines = playerStats.droppedNonGameLines + chatStats.droppedNonGameLines;
  stats.endOffsetsBySource['raw-player'] = playerStats.endOffsets;
  stats.endOffsetsBySource['raw-chat'] = chatStats.endOffsets;
  stats.rolledOverFiles.push(...playerStats.rolledOverFiles, ...chatStats.rolledOverFiles);
}

async function* singleSource(
  config: ServerConfig,
  query: MultiSourceRawQuery,
  stats: MultiSourceRawStats,
): AsyncGenerator<RawLineRecord, void, void> {
  switch (query.source) {
    case 'player': {
      const sub = makeSubStats();
      yield* scanPlayerLogRaw({
        path: config.playerLogPath,
        since: query.since,
        until: query.until,
        prevCursor: query.cursors?.['raw-player']?.perFile?.[config.playerLogPath],
      }, sub);
      stats.scannedBytes = sub.scannedBytes;
      stats.scannedLines = sub.scannedLines;
      stats.droppedNonGameLines = sub.droppedNonGameLines;
      stats.endOffsetsBySource['raw-player'] = sub.endOffsets;
      stats.rolledOverFiles.push(...sub.rolledOverFiles);
      return;
    }
    case 'chat': {
      const sub = makeSubStats();
      yield* scanChatLogsRaw({
        dir: config.chatLogDir,
        since: query.since,
        until: query.until,
        prevCursors: query.cursors?.['raw-chat']?.perFile,
      }, sub);
      stats.scannedBytes = sub.scannedBytes;
      stats.scannedLines = sub.scannedLines;
      stats.droppedNonGameLines = sub.droppedNonGameLines;
      stats.endOffsetsBySource['raw-chat'] = sub.endOffsets;
      stats.rolledOverFiles.push(...sub.rolledOverFiles);
      return;
    }
    default:
      throw new Error(`Unknown raw source: ${query.source}`);
  }
}

function makeSubStats(): PlayerLogRawScanStats & ChatLogRawScanStats {
  return {
    scannedBytes: 0,
    scannedLines: 0,
    droppedNonGameLines: 0,
    endOffsets: {},
    rolledOverFiles: [],
  };
}

export function emptyMultiSourceRawStats(): MultiSourceRawStats {
  return {
    scannedBytes: 0,
    scannedLines: 0,
    droppedNonGameLines: 0,
    endOffsetsBySource: {},
    rolledOverFiles: [],
  };
}

