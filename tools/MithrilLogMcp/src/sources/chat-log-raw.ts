import * as fs from 'node:fs';
import * as path from 'node:path';
import { lineStream } from '../util/file-streams.js';
import { rolloverDetected, type FileCursor } from '../state/cursors.js';
import { CHAT_FILE_RE, CHAT_LINE_RE, chatFileDate, parseChatTimestamp } from './chat-log.js';
import type { RawLineRecord } from './player-log-raw.js';

/**
 * Raw counterpart to `scanChatLogs`. Walks `Chat-YY-MM-DD.log` files in date
 * order, yielding every line whose `YY-MM-DD HH:MM:SS\t` prefix is present
 * within the time window. Files outside the window are skipped without
 * opening; in-window files start at their cursor offset (when valid).
 *
 * Chat lines are well-behaved (the game writes one parseable line per chat
 * message, no Unity boot noise), so `droppedNonGameLines` is typically 0 —
 * but the counter is kept for symmetry with the player scanner.
 */

export interface ChatLogRawQuery {
  dir: string;
  since: Date;
  until: Date;
  prevCursors?: Record<string, FileCursor> | undefined;
}

export interface ChatLogRawScanStats {
  scannedBytes: number;
  scannedLines: number;
  droppedNonGameLines: number;
  endOffsets: Record<string, number>;
  rolledOverFiles: string[];
}

export async function* scanChatLogsRaw(
  query: ChatLogRawQuery,
  stats: ChatLogRawScanStats,
): AsyncGenerator<RawLineRecord, void, void> {
  if (!fs.existsSync(query.dir)) return;

  const entries = fs.readdirSync(query.dir).sort();

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

      if (!CHAT_LINE_RE.test(rec.line)) {
        stats.droppedNonGameLines += 1;
        continue;
      }

      const ts = parseChatTimestamp(rec.line);
      if (!ts) {
        stats.droppedNonGameLines += 1;
        continue;
      }
      if (ts < query.since) continue;
      if (ts > query.until) return;

      yield {
        ts,
        source: 'chat',
        file: fullPath,
        line: rec.lineNo,
        raw: rec.line,
        byteOffset: rec.byteOffset,
      };
    }
  }
}
