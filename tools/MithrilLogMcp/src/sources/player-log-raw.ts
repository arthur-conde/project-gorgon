import * as fs from 'node:fs';
import { lineStream, scannedBytes } from '../util/file-streams.js';
import { countPlayerLogCrossings, PlayerLogTimestamper } from '../util/time-windows.js';
import { rolloverDetected, type FileCursor } from '../state/cursors.js';
import { discoverPlayerLogPaths, type DiscoveredPlayerLog } from './player-log-discovery.js';

/**
 * Raw counterpart to `scanPlayerLog`. Streams every Player.log file
 * (current + Player-prev.log) in age order and yields every line with a
 * `[HH:MM:SS]` prefix — the gameplay-event marker — within the requested
 * time window. Lines without that prefix (Unity boot output, stack-trace
 * addresses, GfxDevice banners, fallback-backend chatter) are counted as
 * `droppedNonGameLines` and dropped silently.
 *
 * Cursor handling, mtime-anchored date stamping, rollover detection, and
 * multi-file discovery mirror `scanPlayerLog` — but the catalog/parser/
 * active-character machinery is dropped because raw streaming has no use
 * for typed events.
 */

export interface PlayerLogRawQuery {
  /** Path to the *current* Player.log. Sibling Player-prev.log is read
   *  automatically when present. */
  path: string;
  since: Date;
  until: Date;
  /** Per-file cursor map (same shape as `CursorState.perFile`). */
  prevCursors?: Record<string, FileCursor> | undefined;
}

export interface PlayerLogRawScanStats {
  scannedBytes: number;
  scannedLines: number;
  droppedNonGameLines: number;
  endOffsets: Record<string, number>;
  rolledOverFiles: string[];
}

export interface RawLineRecord {
  ts: Date;
  source: 'player' | 'chat';
  file: string;
  line: number;
  raw: string;
  byteOffset: number;
}

export async function* scanPlayerLogRaw(
  query: PlayerLogRawQuery,
  stats: PlayerLogRawScanStats,
): AsyncGenerator<RawLineRecord, void, void> {
  const files = discoverPlayerLogPaths(query.path);
  if (files.length === 0) return;

  for (const file of files) {
    yield* scanSinglePlayerFileRaw(file, query, stats);
  }
}

async function* scanSinglePlayerFileRaw(
  file: DiscoveredPlayerLog,
  query: PlayerLogRawQuery,
  stats: PlayerLogRawScanStats,
): AsyncGenerator<RawLineRecord, void, void> {
  const stat = file.stat;
  const filePath = file.path;
  const prev = query.prevCursors?.[filePath];

  let startOffset = 0;
  if (prev) {
    if (rolloverDetected(prev, stat)) {
      stats.rolledOverFiles.push(filePath);
      startOffset = 0;
    } else {
      startOffset = Math.min(prev.byteOffset, stat.size);
    }
  }

  stats.scannedBytes += scannedBytes(stat, startOffset);
  stats.endOffsets[filePath] = startOffset;

  // Anchor the start-of-file date the same way scanPlayerLog does: count
  // midnight crossings, then walk back from mtime UTC date by that many days.
  const crossings = await countPlayerLogCrossings(lineStream(filePath, { start: startOffset }));
  const startDate = new Date(stat.mtime);
  startDate.setUTCDate(startDate.getUTCDate() - crossings);
  const stamper = new PlayerLogTimestamper(startDate);

  for await (const rec of lineStream(filePath, { start: startOffset })) {
    stats.scannedLines += 1;
    stats.endOffsets[filePath] = rec.byteOffset + rec.byteLength;

    if (!PlayerLogTimestamper.TIME_RE.test(rec.line)) {
      stats.droppedNonGameLines += 1;
      continue;
    }

    const ts = stamper.stamp(rec.line, stat.mtime);
    if (ts < query.since) continue;
    if (ts > query.until) break;

    yield {
      ts,
      source: 'player',
      file: filePath,
      line: rec.lineNo,
      raw: rec.line,
      byteOffset: rec.byteOffset,
    };
  }
}
