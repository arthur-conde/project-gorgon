import type { Catalog, CatalogEntry, CatalogField } from './catalog.js';
import type { ParsedEvent } from './types.js';

/**
 * Dispatcher for chat log lines (`YY-MM-DD HH:MM:SS\t[Channel] Speaker: message`).
 * Like {@link PlayerLineParser} but constrained to `source: "chat"` catalog
 * entries. The channel/speaker tokenisation is done by the source layer; this
 * receives whole chat lines and re-runs each chat regex against them.
 */
export class ChatLineParser {
  private readonly entries: CatalogEntry[];

  constructor(catalog: Catalog) {
    this.entries = (catalog.bySource.get('chat') ?? []).filter(
      (e) => e.kind !== 'helper' && e.eventType !== undefined,
    );
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
      const m = entry.regex.exec(line);
      if (!m) continue;
      out.push({
        type: entry.eventType!,
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
}

function extractFields(fields: CatalogField[], m: RegExpExecArray): Record<string, unknown> {
  const data: Record<string, unknown> = {};
  for (const f of fields) {
    const raw = typeof f.group === 'number' ? m[f.group] : m.groups?.[f.group];
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
      if (!Number.isFinite(n) || Math.abs(n) > Number.MAX_SAFE_INTEGER) return raw;
      return n;
    }
    case 'double': {
      const n = Number.parseFloat(raw);
      return Number.isFinite(n) ? n : raw;
    }
    default:
      return raw;
  }
}
