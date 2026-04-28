import * as fs from 'node:fs';
import { lineStream, scannedBytes } from '../util/file-streams.js';
import { countPlayerLogCrossings, PlayerLogTimestamper } from '../util/time-windows.js';
import { rolloverDetected, type FileCursor } from '../state/cursors.js';

/**
 * Raw counterpart to `scanPlayerLog`. Streams Player.log forward and yields
 * every line with a `[HH:MM:SS]` prefix — the gameplay-event marker — within
 * the requested time window. Lines without that prefix (Unity boot output,
 * stack-trace addresses, GfxDevice banners, fallback-backend chatter) are
 * counted as `droppedNonGameLines` and dropped silently.
 *
 * Cursor handling, mtime-anchored date stamping, and rollover detection
 * mirror `scanPlayerLog` — but the catalog/parser/active-character machinery
 * is dropped because raw streaming has no use for typed events.
 */

export interface PlayerLogRawQuery {
  path: string;
  since: Date;
  until: Date;
  prevCursor?: FileCursor | undefined;
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
  if (!fs.existsSync(query.path)) return;

  const stat = fs.statSync(query.path);
  let startOffset = 0;
  if (query.prevCursor) {
    if (rolloverDetected(query.prevCursor, stat)) {
      stats.rolledOverFiles.push(query.path);
      startOffset = 0;
    } else {
      startOffset = Math.min(query.prevCursor.byteOffset, stat.size);
    }
  }

  stats.scannedBytes += scannedBytes(stat, startOffset);
  stats.endOffsets[query.path] = startOffset;

  // Anchor the start-of-file date the same way scanPlayerLog does: count
  // midnight crossings, then walk back from mtime UTC date by that many days.
  const crossings = await countPlayerLogCrossings(lineStream(query.path, { start: startOffset }));
  const startDate = new Date(stat.mtime);
  startDate.setUTCDate(startDate.getUTCDate() - crossings);
  const stamper = new PlayerLogTimestamper(startDate);

  for await (const rec of lineStream(query.path, { start: startOffset })) {
    stats.scannedLines += 1;
    stats.endOffsets[query.path] = rec.byteOffset + rec.byteLength;

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
      file: query.path,
      line: rec.lineNo,
      raw: rec.line,
      byteOffset: rec.byteOffset,
    };
  }
}
