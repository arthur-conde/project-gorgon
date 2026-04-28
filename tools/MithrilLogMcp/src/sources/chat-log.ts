import * as fs from 'node:fs';
import * as path from 'node:path';
import { lineStream } from '../util/file-streams.js';
import { ChatLineParser } from '../parsing/chat-parser.js';
import { rolloverDetected, type FileCursor } from '../state/cursors.js';
import type { ParsedEvent } from '../parsing/types.js';
import type { Catalog } from '../parsing/catalog.js';

export const CHAT_LINE_RE = /^(?<y>\d{2})-(?<m>\d{2})-(?<d>\d{2})\s+(?<H>\d{2}):(?<M>\d{2}):(?<S>\d{2})\s*\t/;
export const CHAT_FILE_RE = /^Chat-(\d{2})-(\d{2})-(\d{2})\.log$/;

export interface ChatLogQuery {
  dir: string;
  since: Date;
  until: Date;
  /** Per-file cursors keyed by absolute path. Stale entries trigger rollover. */
  prevCursors?: Record<string, FileCursor> | undefined;
}

export interface ChatScanStats {
  scannedBytes: number;
  scannedLines: number;
  endOffsets: Record<string, number>;
  rolledOverFiles: string[];
}

/**
 * Streams Mithril's `ChatLogs/Chat-YY-MM-DD.log` files in chronological order,
 * yielding parsed events within the time window. Files outside the window (by
 * filename date) are skipped without opening; in-window files start at their
 * cursor offset (when available and not stale).
 */
export async function* scanChatLogs(
  catalog: Catalog,
  query: ChatLogQuery,
  stats: ChatScanStats,
): AsyncGenerator<ParsedEvent, void, void> {
  if (!fs.existsSync(query.dir)) return;

  const entries = fs.readdirSync(query.dir).sort();
  const parser = new ChatLineParser(catalog);

  for (const name of entries) {
    const m = CHAT_FILE_RE.exec(name);
    if (!m) continue;

    const fileDate = chatFileDate(m[1]!, m[2]!, m[3]!);
    const dayStart = new Date(fileDate.getTime());
    const dayEnd = new Date(fileDate.getTime() + 24 * 3_600_000);
    if (dayEnd < query.since) continue;
    if (dayStart > query.until) continue;

    const fullPath = path.join(query.dir, name);
    const stat = fs.statSync(fullPath);
    const prev = query.prevCursors?.[fullPath];

    let startOffset = 0;
    if (prev) {
      if (rolloverDetected(prev, stat)) {
        stats.rolledOverFiles.push(fullPath);
      } else {
        startOffset = Math.min(prev.byteOffset, stat.size);
      }
    }

    stats.scannedBytes += Math.max(0, stat.size - startOffset);
    stats.endOffsets[fullPath] = startOffset;

    for await (const rec of lineStream(fullPath, { start: startOffset })) {
      stats.scannedLines += 1;
      stats.endOffsets[fullPath] = rec.byteOffset + rec.byteLength;
      const ts = parseChatTimestamp(rec.line);
      if (!ts) continue;
      if (ts < query.since) continue;
      if (ts > query.until) return;
      for (const ev of parser.parse(rec.line, ts, fullPath, rec.lineNo, rec.byteOffset)) {
        yield ev;
      }
    }
  }
}

export function chatFileDate(y: string, m: string, d: string): Date {
  const year = 2000 + Number.parseInt(y, 10);
  const month = Number.parseInt(m, 10) - 1;
  const day = Number.parseInt(d, 10);
  return new Date(Date.UTC(year, month, day));
}

export function parseChatTimestamp(line: string): Date | null {
  const m = CHAT_LINE_RE.exec(line);
  if (!m) return null;
  const g = m.groups!;
  const year = 2000 + Number.parseInt(g.y!, 10);
  const month = Number.parseInt(g.m!, 10) - 1;
  const day = Number.parseInt(g.d!, 10);
  const H = Number.parseInt(g.H!, 10);
  const M = Number.parseInt(g.M!, 10);
  const S = Number.parseInt(g.S!, 10);
  // The game writes chat timestamps in the user's local timezone. Use the
  // local Date constructor so the resulting UTC instant matches the wall
  // clock the user sees.
  return new Date(year, month, day, H, M, S);
}
