import { z } from 'zod';
import { loadCatalog } from '../parsing/catalog.js';

export const ListEventTypesInput = z.object({
  source: z.enum(['player', 'chat', 'all']).default('all'),
});

export type ListEventTypesArgs = z.infer<typeof ListEventTypesInput>;

export function runListEventTypes(args: ListEventTypesArgs) {
  const catalog = loadCatalog();
  const filtered = catalog.entries.filter(
    (e) => args.source === 'all' || e.source === args.source,
  );

  const seen = new Map<string, {
    type: string;
    source: string;
    module: string;
    fields: { name: string; type: string }[];
  }>();
  for (const e of filtered) {
    if (!e.eventType) continue;
    if (e.kind === 'helper') continue;
    if (seen.has(e.eventType)) continue;
    seen.set(e.eventType, {
      type: e.eventType,
      source: e.source,
      module: e.module,
      fields: e.fields.map((f) => ({ name: f.name, type: f.type })),
    });
  }

  const types = Array.from(seen.values()).sort((a, b) => a.type.localeCompare(b.type));
  return { types };
}
