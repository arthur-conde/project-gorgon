import * as fs from 'node:fs';
import * as path from 'node:path';
import { lineStream } from '../util/file-streams.js';
import { ChatLineParser } from '../parsing/chat-parser.js';
import type { ParsedEvent } from '../parsing/types.js';
import type { Catalog } from '../parsing/catalog.js';

const CHAT_LINE_RE = /^(?<y>\d{2})-(?<m>\d{2})-(?<d>\d{2})\s+(?<H>\d{2}):(?<M>\d{2}):(?<S>\d{2})\s*\t/;
const CHAT_FILE_RE = /^Chat-(\d{2})-(\d{2})-(\d{2})\.log$/;

export interface ChatLogQuery {
  dir: string;
  since: Date;
  until: Date;
}

export interface ChatScanStats {
  scannedBytes: number;
  scannedLines: number;
}

/**
 * Streams Mithril's `ChatLogs/Chat-YY-MM-DD.log` files in chronological order,
 * yielding parsed events that fall within the requested time window. Files
 * outside the window (by filename date) are skipped without opening.
 *
 * The chat line itself carries a full `YY-MM-DD HH:MM:SS` timestamp, which
 * we parse directly — no mtime anchor needed (unlike Player.log).
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
    // Skip files entirely outside the window. Conservative: keep the file if any
    // hour of its day overlaps with [since, until].
    const dayStart = new Date(fileDate.getTime());
    const dayEnd = new Date(fileDate.getTime() + 24 * 3_600_000);
    if (dayEnd < query.since) continue;
    if (dayStart > query.until) continue;

    const fullPath = path.join(query.dir, name);
    const stat = fs.statSync(fullPath);
    stats.scannedBytes += stat.size;

    for await (const rec of lineStream(fullPath)) {
      stats.scannedLines += 1;
      const ts = parseChatTimestamp(rec.line);
      if (!ts) continue;
      if (ts < query.since) continue;
      if (ts > query.until) return; // sorted, so anything past is also past
      for (const ev of parser.parse(rec.line, ts, fullPath, rec.lineNo, rec.byteOffset)) {
        yield ev;
      }
    }
  }
}

function chatFileDate(y: string, m: string, d: string): Date {
  // Two-digit year: 26 -> 2026. Bake in the simple +2000 convention; revisit
  // if Project Gorgon's filename format ever changes.
  const year = 2000 + Number.parseInt(y, 10);
  const month = Number.parseInt(m, 10) - 1;
  const day = Number.parseInt(d, 10);
  return new Date(Date.UTC(year, month, day));
}

function parseChatTimestamp(line: string): Date | null {
  const m = CHAT_LINE_RE.exec(line);
  if (!m) return null;
  const g = m.groups!;
  const year = 2000 + Number.parseInt(g.y!, 10);
  const month = Number.parseInt(g.m!, 10) - 1;
  const day = Number.parseInt(g.d!, 10);
  const H = Number.parseInt(g.H!, 10);
  const M = Number.parseInt(g.M!, 10);
  const S = Number.parseInt(g.S!, 10);
  return new Date(Date.UTC(year, month, day, H, M, S));
}
