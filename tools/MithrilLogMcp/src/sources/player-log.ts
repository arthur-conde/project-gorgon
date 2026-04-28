import * as fs from 'node:fs';
import { lineStream, scannedBytes } from '../util/file-streams.js';
import { countPlayerLogCrossings, PlayerLogTimestamper } from '../util/time-windows.js';
import { PlayerLineParser } from '../parsing/player-parser.js';
import { ActiveCharacterTracker } from '../parsing/active-character-tracker.js';
import { rolloverDetected, type FileCursor } from '../state/cursors.js';
import { findActiveCharacterAt } from '../state/character-resolver.js';
import { discoverPlayerLogPaths, type DiscoveredPlayerLog } from './player-log-discovery.js';
import type { ParsedEvent } from '../parsing/types.js';
import type { Catalog } from '../parsing/catalog.js';

export interface PlayerLogQuery {
  /** Path to the *current* Player.log. The scanner discovers Player-prev.log
   *  beside it automatically and reads both in chronological order. */
  path: string;
  since: Date;
  until: Date;
  /** Per-file cursor map (same shape as `CursorState.perFile`). Each file's
   *  entry is honoured independently; missing entries restart from byte 0. */
  prevCursors?: Record<string, FileCursor> | undefined;
  /**
   * Number of raw lines of surrounding context to attach to each emitted
   * event as `contextLines: { before, after }`. 0 disables (default).
   * Implemented as a ring buffer of the last N lines (before) plus a
   * pending queue that holds events until N more lines are read (after).
   */
  context?: number | undefined;
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
 * Streams every Player.log file in age order (oldest first), yielding parsed
 * events within the requested time window. Discovers `Player-prev.log` next
 * to the current `Player.log` so a multi-day query window catches events
 * from the previous session. Each file gets its own cursor lookup; the
 * `ActiveCharacterTracker` is shared across files so a `ProcessAddPlayer`
 * in `Player-prev.log` keeps tagging events in `Player.log`.
 *
 * Ordering invariant for the multi-source merge: rotation guarantees the
 * last line of `Player-prev.log` precedes the first line of `Player.log`,
 * so streaming oldest-first preserves global ascending `ts`.
 */
export async function* scanPlayerLog(
  catalog: Catalog,
  query: PlayerLogQuery,
  stats: PlayerLogScanStats,
): AsyncGenerator<ParsedEvent, void, void> {
  const files = discoverPlayerLogPaths(query.path);
  if (files.length === 0) return;

  const tracker = new ActiveCharacterTracker();

  for (const file of files) {
    yield* scanSinglePlayerFile(catalog, file, query, stats, tracker);
  }
}

async function* scanSinglePlayerFile(
  catalog: Catalog,
  file: DiscoveredPlayerLog,
  query: PlayerLogQuery,
  stats: PlayerLogScanStats,
  tracker: ActiveCharacterTracker,
): AsyncGenerator<ParsedEvent, void, void> {
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

  const parser = new PlayerLineParser(catalog);

  // Anchor the *start* of the file, not the end. A first pass counts midnight
  // crossings; the start-of-file date is mtime UTC date - crossings. The
  // forward scan then advances the date as it sees its own crossings, so the
  // last line of the file ends up stamped at the mtime UTC date — the only
  // value we know for certain.
  const crossings = await countPlayerLogCrossings(lineStream(filePath, { start: startOffset }));
  const startDate = new Date(stat.mtime);
  startDate.setUTCDate(startDate.getUTCDate() - crossings);
  const stamper = new PlayerLogTimestamper(startDate);

  // Seed the active-character tracker from a backward scan when resuming
  // mid-stream — without it, every event before the next ProcessAddPlayer
  // would be unstamped and dropped by `character: "X"` filters. Skip when
  // the tracker already has a value carried over from an earlier file.
  if (startOffset > 0 && !tracker.active) {
    const initialActive = findActiveCharacterAt(filePath, startOffset, catalog);
    if (initialActive) tracker.set(initialActive);
  }

  const N = query.context ?? 0;
  const ringBefore: string[] = [];
  // Each entry's `after` aliases ev.contextLines.after, so pushing to it
  // mutates the event's own array — no second pass needed at yield time.
  const pending: Array<{ ev: ParsedEvent; after: string[] }> = [];

  for await (const rec of lineStream(filePath, { start: startOffset })) {
    // Feed this line as after-context to events still waiting; yield any
    // whose after-window is now full.
    if (N > 0) {
      for (const p of pending) {
        if (p.after.length < N) p.after.push(rec.line);
      }
      while (pending.length > 0 && pending[0]!.after.length >= N) {
        yield pending.shift()!.ev;
      }
    }

    stats.scannedLines += 1;
    stats.endOffsets[filePath] = rec.byteOffset + rec.byteLength;
    const ts = stamper.stamp(rec.line, stat.mtime);
    if (ts < query.since) {
      if (N > 0) pushRing(ringBefore, rec.line, N);
      continue;
    }
    if (ts > query.until) break;

    for (const ev of parser.parse(rec.line, ts, filePath, rec.lineNo, rec.byteOffset)) {
      tracker.observe(ev);
      if (tracker.active) ev.activeCharacter = tracker.active;
      if (N > 0) {
        ev.contextLines = { before: [...ringBefore], after: [] };
        pending.push({ ev, after: ev.contextLines.after });
      } else {
        yield ev;
      }
    }

    if (N > 0) pushRing(ringBefore, rec.line, N);
  }

  // Drain whatever events are still waiting for their full after-context.
  // They get whatever was accumulated before the scan ended.
  while (pending.length > 0) {
    yield pending.shift()!.ev;
  }
}

function pushRing(ring: string[], line: string, max: number): void {
  if (max <= 0) return;
  ring.push(line);
  while (ring.length > max) ring.shift();
}
