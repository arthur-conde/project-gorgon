import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * Loads `src/Mithril.Shared/Reference/log-patterns.json` — the single source of truth
 * for every log-line regex used by Mithril parsers. The .NET parsers and this TS
 * server consume the same JSON; parity with the C# `[GeneratedRegex]` attributes is
 * enforced by the LogPatternCatalogParityTests xunit test in the parent solution.
 */

export interface CatalogDocument {
  version: number;
  regexes: Record<string, CatalogEntryRaw>;
  shared?: {
    sessionMarker?: { literal: string; scanChunkBytes: number; notes?: string };
    playerLogTimestampPrefix?: { regex: string; notes?: string };
  };
}

interface CatalogEntryRaw {
  pattern: string;
  source: string;
  module: string;
  csharp?: { type: string; method: string; options?: string[] };
  eventType?: string;
  kind?: 'helper' | 'compound' | 'marker' | 'session-marker';
  flags?: string[];
  notes?: string;
  fields: CatalogFieldRaw[];
}

interface CatalogFieldRaw {
  name: string;
  group: number | string;
  type: string;
}

export interface CatalogEntry {
  key: string;
  pattern: string;
  regex: RegExp;
  source: string;
  module: string;
  eventType: string | undefined;
  kind: 'helper' | 'compound' | 'marker' | 'session-marker' | undefined;
  fields: CatalogField[];
  notes: string | undefined;
}

export interface CatalogField {
  name: string;
  group: number | string;
  type: string;
}

export interface Catalog {
  document: CatalogDocument;
  entries: CatalogEntry[];
  bySource: Map<string, CatalogEntry[]>;
  byKey: Map<string, CatalogEntry>;
  byEventType: Map<string, CatalogEntry[]>;
  sessionMarker: { literal: string; scanChunkBytes: number };
}

let cached: Catalog | undefined;

export function loadCatalog(jsonPath?: string): Catalog {
  if (cached && !jsonPath) return cached;
  const resolved = jsonPath ?? resolveDefaultPath();
  const raw = fs.readFileSync(resolved, 'utf8');
  const doc = JSON.parse(stripJsonComments(raw)) as CatalogDocument;

  const entries: CatalogEntry[] = [];
  for (const [key, raw] of Object.entries(doc.regexes)) {
    entries.push({
      key,
      pattern: raw.pattern,
      regex: compileRegex(raw.pattern, raw.flags),
      source: raw.source,
      module: raw.module,
      eventType: raw.eventType,
      kind: raw.kind,
      fields: raw.fields ?? [],
      notes: raw.notes,
    });
  }

  const bySource = new Map<string, CatalogEntry[]>();
  const byKey = new Map<string, CatalogEntry>();
  const byEventType = new Map<string, CatalogEntry[]>();
  for (const e of entries) {
    byKey.set(e.key, e);
    if (!bySource.has(e.source)) bySource.set(e.source, []);
    bySource.get(e.source)!.push(e);
    if (e.eventType) {
      if (!byEventType.has(e.eventType)) byEventType.set(e.eventType, []);
      byEventType.get(e.eventType)!.push(e);
    }
  }

  const result: Catalog = {
    document: doc,
    entries,
    bySource,
    byKey,
    byEventType,
    sessionMarker: {
      literal: doc.shared?.sessionMarker?.literal ?? 'ProcessAddPlayer(',
      scanChunkBytes: doc.shared?.sessionMarker?.scanChunkBytes ?? 10 * 1024 * 1024,
    },
  };

  if (!jsonPath) cached = result;
  return result;
}

function compileRegex(pattern: string, flags: string[] | undefined): RegExp {
  const jsFlags = (flags ?? []).join('');
  return new RegExp(pattern, jsFlags);
}

function stripJsonComments(input: string): string {
  // Tolerate the catalog's `$comment` and trailing whitespace; JSON.parse handles
  // standard JSON only. Real comments aren't allowed in the file, so this is a no-op
  // — kept here for forward compatibility if a future schema introduces them.
  return input;
}

function resolveDefaultPath(): string {
  const envPath = process.env.MITHRIL_LOG_PATTERNS_PATH;
  if (envPath && fs.existsSync(envPath)) return envPath;

  const here = path.dirname(fileURLToPath(import.meta.url));
  const repoRoot = findRepoRoot(here);
  return path.join(repoRoot, 'src', 'Mithril.Shared', 'Reference', 'log-patterns.json');
}

function findRepoRoot(start: string): string {
  let dir = start;
  while (dir !== path.dirname(dir)) {
    if (fs.existsSync(path.join(dir, 'Mithril.slnx'))) return dir;
    dir = path.dirname(dir);
  }
  throw new Error(
    `Could not find repo root (no Mithril.slnx) walking up from ${start}. ` +
    'Set MITHRIL_LOG_PATTERNS_PATH to point at log-patterns.json.',
  );
}
