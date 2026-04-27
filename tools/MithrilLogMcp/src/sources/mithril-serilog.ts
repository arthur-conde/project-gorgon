import * as fs from 'node:fs';
import * as path from 'node:path';
import { lineStream } from '../util/file-streams.js';
import { rolloverDetected, type FileCursor } from '../state/cursors.js';
import type { ParsedEvent } from '../parsing/types.js';

export interface MithrilSerilogQuery {
  dir: string;
  since: Date;
  until: Date;
  prevCursors?: Record<string, FileCursor> | undefined;
  /**
   * If provided, pre-filter raw lines on a substring `"Category":"<name>"`
   * before parsing JSON. Skips ~95% of lines for category-specific queries
   * on multi-GB Serilog dumps. The full `event_type` filter still runs in
   * the tool layer; this is an opportunistic shortcut.
   */
  categoryAllowlist?: ReadonlySet<string> | undefined;
}

export interface MithrilSerilogScanStats {
  scannedBytes: number;
  scannedLines: number;
  endOffsets: Record<string, number>;
  rolledOverFiles: string[];
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
 * Streams Mithril's own Serilog output (CompactJsonFormatter, one record per line).
 * Detection is by content (first line has `@t`), so the legacy
 * `gorgon-*.json` files migrated to `mithril-*-prebrand.json` and the current
 * `mithril-*.json` files are all picked up under one glob.
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
    const prev = query.prevCursors?.[e.full];
    let startOffset = 0;
    if (prev) {
      if (rolloverDetected(prev, stat)) {
        stats.rolledOverFiles.push(e.full);
      } else {
        startOffset = Math.min(prev.byteOffset, stat.size);
      }
    }

    stats.scannedBytes += Math.max(0, stat.size - startOffset);
    stats.endOffsets[e.full] = startOffset;

    // Pre-build the category-substring allowlist so we don't re-format the
    // probe string per line. CompactJsonFormatter emits keys in stable order,
    // so an exact `"Category":"X"` substring is reliable.
    const categoryProbes = query.categoryAllowlist
      ? Array.from(query.categoryAllowlist, (c) => `"Category":"${c}"`)
      : null;

    for await (const rec of lineStream(e.full, { start: startOffset })) {
      stats.scannedLines += 1;
      stats.endOffsets[e.full] = rec.byteOffset + rec.byteLength;
      if (rec.line.length === 0) continue;

      // Cheap pre-filter: skip lines whose Category isn't in the allowlist.
      // Saves a JSON.parse per skipped line; on a 1GB Serilog dump filtered
      // to one Category, this is ~10x throughput.
      if (categoryProbes && !categoryProbes.some((p) => rec.line.includes(p))) {
        continue;
      }

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
