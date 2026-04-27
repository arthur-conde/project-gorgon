import * as fs from 'node:fs';
import { lineStream, scannedBytes } from '../util/file-streams.js';
import { PlayerLogTimestamper } from '../util/time-windows.js';
import { PlayerLineParser } from '../parsing/player-parser.js';
import { ActiveCharacterTracker } from '../parsing/active-character-tracker.js';
import { rolloverDetected, type FileCursor } from '../state/cursors.js';
import { findActiveCharacterAt } from '../state/character-resolver.js';
import type { ParsedEvent } from '../parsing/types.js';
import type { Catalog } from '../parsing/catalog.js';

export interface PlayerLogQuery {
  path: string;
  since: Date;
  until: Date;
  /** Cursor entry for this file, if any. Honoured when not stale. */
  prevCursor?: FileCursor | undefined;
}

export interface PlayerLogScanStats {
  scannedBytes: number;
  scannedLines: number;
  /** Final byte offset reached per file. Used to advance cursors. */
  endOffsets: Record<string, number>;
  /** Files whose previous cursor was stale (rolled over) and reset to 0. */
  rolledOverFiles: string[];
}

/**
 * Streams Player.log forward, yielding parsed events within the requested
 * time window. If `prevCursor` is supplied and still valid (no rollover),
 * scanning starts at its byteOffset; otherwise from byte 0. The final reached
 * byte offset is recorded in `stats.endOffsets[path]` so callers can persist
 * it as the next cursor.
 */
export async function* scanPlayerLog(
  catalog: Catalog,
  query: PlayerLogQuery,
  stats: PlayerLogScanStats,
): AsyncGenerator<ParsedEvent, void, void> {
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

  const parser = new PlayerLineParser(catalog);
  const stamper = new PlayerLogTimestamper(stat.mtime);

  // Seed the active-character tracker from a backward scan when resuming
  // mid-stream — without it, every event before the next ProcessAddPlayer
  // would be unstamped and dropped by `character: "X"` filters.
  const initialActive = startOffset > 0
    ? findActiveCharacterAt(query.path, startOffset, catalog)
    : null;
  const tracker = new ActiveCharacterTracker(initialActive ?? undefined);

  for await (const rec of lineStream(query.path, { start: startOffset })) {
    stats.scannedLines += 1;
    stats.endOffsets[query.path] = rec.byteOffset + rec.byteLength;
    const ts = stamper.stamp(rec.line, stat.mtime);
    if (ts < query.since) continue;
    if (ts > query.until) break;
    for (const ev of parser.parse(rec.line, ts, query.path, rec.lineNo, rec.byteOffset)) {
      tracker.observe(ev);
      if (tracker.active) ev.activeCharacter = tracker.active;
      yield ev;
    }
  }
}
