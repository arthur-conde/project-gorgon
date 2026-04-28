import * as fs from 'node:fs';
import { CursorStore, snapshotCursor, type CursorState } from './cursors.js';
import type { MultiSourceStats } from '../sources/multi-source.js';

/**
 * Reusable cursor read/persist plumbing shared by `query_events` and
 * `aggregate`. The store is the durable state; this module is the
 * tool-side adapter that translates between MCP tool args and the
 * per-source `CursorState` shape the scanners consume.
 */

const SOURCES = ['player', 'chat', 'mithril', 'raw-player', 'raw-chat'] as const;

/**
 * Loads every per-source cursor record for a named cursor in one shot.
 * Returns `undefined` when no cursor was requested so callers can branch
 * cleanly (`undefined` -> "fresh scan from time window only").
 */
export function loadCursorsForName(
  store: CursorStore,
  name: string | undefined,
): Record<string, CursorState> | undefined {
  if (!name) return undefined;
  const out: Record<string, CursorState> = {};
  for (const s of SOURCES) out[s] = store.get(name, s);
  return out;
}

/**
 * Snapshots the per-file end offsets reached during a successful scan and
 * persists them as the new state for `name`. Atomic via the store's
 * write-temp-then-rename. Files that disappeared between scan and persist
 * are silently skipped — the next call will treat them as a fresh read.
 */
export function persistCursor(
  store: CursorStore,
  name: string,
  stats: MultiSourceStats,
): void {
  for (const [source, endOffsets] of Object.entries(stats.endOffsetsBySource)) {
    const perFile: CursorState['perFile'] = {};
    for (const [file, offset] of Object.entries(endOffsets)) {
      try {
        const stat = fs.statSync(file);
        perFile[file] = snapshotCursor(stat, offset);
      } catch {
        // File disappeared between scan and persist — skip; next call
        // will treat it as a fresh read.
      }
    }
    store.put(name, source, { perFile });
  }
}

/** Total number of files whose offsets were advanced this scan. */
export function countAdvancedFiles(stats: MultiSourceStats): number {
  let n = 0;
  for (const eo of Object.values(stats.endOffsetsBySource)) {
    n += Object.keys(eo).length;
  }
  return n;
}
