import type { Catalog, CatalogEntry, CatalogField } from './catalog.js';
import type { ParsedEvent } from './types.js';

/**
 * Dispatches a single Player.log line through every catalog regex tagged
 * `source: "player"`. Returns the typed events that matched (zero, one, or
 * many — different modules can match the same line for different reasons).
 *
 * Pippin's `ProcessBook("Skill Info", ...)` line is handled as a compound
 * event: the outer match captures the report body, then sub-entries are
 * extracted with `FoodEntryRx` and aggregated into a single
 * `pippin.FoodsConsumedReport` event with a `foods[]` array.
 */
export class PlayerLineParser {
  private readonly entries: CatalogEntry[];
  private readonly foodEntryRegex: RegExp | undefined;

  constructor(catalog: Catalog) {
    this.entries = (catalog.bySource.get('player') ?? []).filter(
      (e) => e.kind !== 'helper',
    );
    this.foodEntryRegex = catalog.byKey.get(
      'pippin.GourmandLogParser.FoodEntryRx',
    )?.regex;
  }

  parse(
    line: string,
    timestamp: Date,
    file: string,
    lineNo: number,
    byteOffset: number,
  ): ParsedEvent[] {
    if (line.length === 0) return [];

    const out: ParsedEvent[] = [];
    for (const entry of this.entries) {
      if (!entry.eventType) continue;

      // Reset regex state for global regexes; safer to use exec with a fresh start
      // by always compiling without /g. (catalog.compileRegex enforces this.)
      const m = entry.regex.exec(line);
      if (!m) continue;

      if (entry.kind === 'compound') {
        const compound = this.handleCompound(entry, m, timestamp, file, lineNo, byteOffset);
        if (compound) out.push(compound);
        continue;
      }

      out.push({
        type: entry.eventType,
        ts: timestamp.toISOString(),
        module: entry.module,
        source: entry.source,
        file,
        line: lineNo,
        byteOffset,
        data: extractFields(entry.fields, m),
      });
    }
    return out;
  }

  private handleCompound(
    entry: CatalogEntry,
    outerMatch: RegExpExecArray,
    timestamp: Date,
    file: string,
    lineNo: number,
    byteOffset: number,
  ): ParsedEvent | null {
    if (entry.eventType !== 'pippin.FoodsConsumedReport') return null;
    if (!this.foodEntryRegex) return null;

    // Group 1 of ProcessBookFoodsRx is the body; entries are separated by a
    // literal two-char `\n` sequence (backslash + n), not real newlines.
    const body = outerMatch[1] ?? '';
    const rawEntries = body.split('\\n').filter((s) => s.length > 0);
    const foods: Array<{ name: string; count: number; tags: string[] }> = [];

    for (const raw of rawEntries) {
      const m = this.foodEntryRegex.exec(raw);
      if (!m) continue;
      const name = (m[1] ?? '').trim();
      const tagsRaw = m[2];
      const countStr = m[3] ?? '0';
      const count = Number.parseInt(countStr, 10);
      if (!Number.isFinite(count)) continue;

      const tags: string[] = tagsRaw
        ? tagsRaw.split(',')
            .map((t) => t.trim())
            .map((t) => (t.toUpperCase().startsWith('HAS ') ? t.slice(4).trim() : t))
        : [];

      foods.push({ name, count, tags });
    }

    if (foods.length === 0) return null;
    return {
      type: 'pippin.FoodsConsumedReport',
      ts: timestamp.toISOString(),
      module: entry.module,
      source: entry.source,
      file,
      line: lineNo,
      byteOffset,
      data: { foods },
    };
  }
}

function extractFields(fields: CatalogField[], m: RegExpExecArray): Record<string, unknown> {
  const data: Record<string, unknown> = {};
  for (const f of fields) {
    const raw =
      typeof f.group === 'number'
        ? m[f.group]
        : (m.groups?.[f.group] ?? undefined);
    if (raw === undefined) continue;
    data[f.name] = coerce(raw, f.type);
  }
  return data;
}

function coerce(raw: string, type: string): unknown {
  switch (type) {
    case 'int': {
      const n = Number.parseInt(raw, 10);
      return Number.isFinite(n) ? n : raw;
    }
    case 'long': {
      const n = Number.parseInt(raw, 10);
      // JSON cannot safely round-trip integers > 2^53. For Project Gorgon's
      // long fields (entity ids, instance ids), values usually fit; emit as a
      // string when out of range so consumers don't silently lose precision.
      if (!Number.isFinite(n) || Math.abs(n) > Number.MAX_SAFE_INTEGER) return raw;
      return n;
    }
    case 'double': {
      const n = Number.parseFloat(raw);
      return Number.isFinite(n) ? n : raw;
    }
    case 'string':
    default:
      return raw;
  }
}
