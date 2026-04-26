import * as fs from 'node:fs';
import * as path from 'node:path';
import { lineStream } from '../util/file-streams.js';
import type { ParsedEvent } from '../parsing/types.js';

export interface MithrilSerilogQuery {
  dir: string;
  since: Date;
  until: Date;
}

export interface MithrilSerilogScanStats {
  scannedBytes: number;
  scannedLines: number;
}

interface CompactSerilogLine {
  '@t'?: string;
  '@mt'?: string;
  '@m'?: string;
  '@l'?: string;
  '@x'?: string;
  Category?: string;
  Message?: string;
  [k: string]: unknown;
}

/**
 * Streams Mithril's own Serilog output (CompactJsonFormatter, one record per line,
 * `@t`/`@mt`/`Category`/`Message` fields). Glob is `*.json` so the historical
 * `gorgon-*.json` files captured by the legacy-log migration step are also
 * indexed alongside the renamed `mithril-*-prebrand.json` and current `mithril-*.json`.
 *
 * Detection: try `JSON.parse` on the first non-empty line; if it parses and has
 * `@t`, treat as a Serilog file. Otherwise skip — we don't fall back to a
 * regex parser for v0.2.
 */
export async function* scanMithrilSerilog(
  query: MithrilSerilogQuery,
  stats: MithrilSerilogScanStats,
): AsyncGenerator<ParsedEvent, void, void> {
  if (!fs.existsSync(query.dir)) return;
  const entries = fs.readdirSync(query.dir)
    .filter((n) => n.endsWith('.json'))
    .map((n) => ({ name: n, full: path.join(query.dir, n) }))
    .sort((a, b) => a.name.localeCompare(b.name));

  for (const e of entries) {
    if (!(await fileLooksLikeSerilog(e.full))) continue;

    const stat = fs.statSync(e.full);
    stats.scannedBytes += stat.size;

    for await (const rec of lineStream(e.full)) {
      stats.scannedLines += 1;
      if (rec.line.length === 0) continue;

      let parsed: CompactSerilogLine;
      try {
        parsed = JSON.parse(rec.line) as CompactSerilogLine;
      } catch {
        continue;
      }

      const tsRaw = parsed['@t'];
      if (typeof tsRaw !== 'string') continue;
      const ts = new Date(tsRaw);
      if (!Number.isFinite(ts.getTime())) continue;
      if (ts < query.since) continue;
      if (ts > query.until) break;

      const category = typeof parsed.Category === 'string' ? parsed.Category : 'Unknown';
      const message = typeof parsed.Message === 'string'
        ? parsed.Message
        : (typeof parsed['@m'] === 'string' ? parsed['@m'] : '');

      const data: Record<string, unknown> = { category, message };
      const level = parsed['@l'];
      if (typeof level === 'string') data.level = level;
      const exception = parsed['@x'];
      if (typeof exception === 'string') data.exception = exception;

      // Surface any non-Serilog-internal fields verbatim so structured log
      // properties remain queryable by downstream tools.
      for (const [k, v] of Object.entries(parsed)) {
        if (k.startsWith('@')) continue;
        if (k === 'Category' || k === 'Message') continue;
        data[k] = v;
      }

      yield {
        type: `mithril.${category}`,
        ts: ts.toISOString(),
        module: 'mithril',
        source: 'mithril',
        file: e.full,
        line: rec.lineNo,
        byteOffset: rec.byteOffset,
        data,
      };
    }
  }
}

async function fileLooksLikeSerilog(file: string): Promise<boolean> {
  for await (const rec of lineStream(file)) {
    if (rec.line.length === 0) continue;
    try {
      const obj = JSON.parse(rec.line) as Record<string, unknown>;
      return typeof obj['@t'] === 'string';
    } catch {
      return false;
    }
  }
  return false;
}
