import * as fs from 'node:fs';
import { lineStream, scannedBytes } from '../util/file-streams.js';
import { PlayerLogTimestamper } from '../util/time-windows.js';
import { PlayerLineParser } from '../parsing/player-parser.js';
import type { ParsedEvent } from '../parsing/types.js';
import type { Catalog } from '../parsing/catalog.js';

export interface PlayerLogQuery {
  path: string;
  since: Date;
  until: Date;
}

export interface PlayerLogScanStats {
  scannedBytes: number;
  scannedLines: number;
}

/**
 * Streams Player.log forward, yielding parsed events that fall within the
 * requested time window. v0.1 does a full forward scan from byte 0; the
 * binary-search optimisation for huge files lives in v0.3 once cursors land.
 */
export async function* scanPlayerLog(
  catalog: Catalog,
  query: PlayerLogQuery,
  stats: PlayerLogScanStats,
): AsyncGenerator<ParsedEvent, void, void> {
  if (!fs.existsSync(query.path)) return;

  const stat = fs.statSync(query.path);
  stats.scannedBytes = scannedBytes(stat);

  const parser = new PlayerLineParser(catalog);
  const stamper = new PlayerLogTimestamper(stat.mtime);

  for await (const rec of lineStream(query.path)) {
    stats.scannedLines += 1;
    const ts = stamper.stamp(rec.line, stat.mtime);
    if (ts < query.since) continue;
    if (ts > query.until) break;
    for (const ev of parser.parse(rec.line, ts, query.path, rec.lineNo, rec.byteOffset)) {
      yield ev;
    }
  }
}
