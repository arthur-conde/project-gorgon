import * as fs from 'node:fs';
import { z } from 'zod';
import { CursorStore, snapshotCursor, type CursorState } from '../state/cursors.js';

export const CursorListInput = z.object({});
export const CursorResetInput = z.object({
  name: z.string().min(1),
});

/**
 * Manually positions a named cursor for one source.
 *
 * `anchor: "start"` — byte 0 (re-read everything).
 * `anchor: "end"`   — current end of file (skip everything before now).
 * `anchor: "offset"` — exact byte offset in `byteOffset`.
 *
 * Useful for "fast-forward to now" and "rewind a cursor that I want to
 * re-read from scratch without losing the cursor name".
 */
export const CursorSetInput = z.object({
  name: z.string().min(1),
  source: z.enum(['player', 'chat', 'mithril']),
  file: z.string().min(1),
  anchor: z.enum(['start', 'end', 'offset']).default('start'),
  byteOffset: z.number().int().min(0).optional(),
});

export type CursorListArgs = z.infer<typeof CursorListInput>;
export type CursorResetArgs = z.infer<typeof CursorResetInput>;
export type CursorSetArgs = z.infer<typeof CursorSetInput>;

export function runCursorList(_args: CursorListArgs, store: CursorStore) {
  const cursors = store.list().map((c) => ({
    name: c.name,
    sources: Object.fromEntries(
      Object.entries(c.sources).map(([source, state]) => [
        source,
        {
          fileCount: Object.keys(state.perFile).length,
          files: Object.fromEntries(
            Object.entries(state.perFile).map(([f, fc]) => [f, fc.byteOffset]),
          ),
        },
      ]),
    ),
  }));
  return { cursors };
}

export function runCursorReset(args: CursorResetArgs, store: CursorStore) {
  store.reset(args.name);
  return { ok: true, name: args.name };
}

export function runCursorSet(args: CursorSetArgs, store: CursorStore) {
  if (!fs.existsSync(args.file)) {
    throw new Error(`File not found: ${args.file}`);
  }
  const stat = fs.statSync(args.file);

  let offset: number;
  switch (args.anchor) {
    case 'start': offset = 0; break;
    case 'end': offset = stat.size; break;
    case 'offset':
      if (args.byteOffset === undefined) {
        throw new Error("anchor='offset' requires byteOffset");
      }
      if (args.byteOffset > stat.size) {
        throw new Error(
          `byteOffset ${args.byteOffset} exceeds file size ${stat.size}`,
        );
      }
      offset = args.byteOffset;
      break;
  }

  const existing = store.get(args.name, args.source);
  const next: CursorState = {
    perFile: { ...existing.perFile, [args.file]: snapshotCursor(stat, offset) },
  };
  store.put(args.name, args.source, next);
  return {
    ok: true,
    name: args.name,
    source: args.source,
    file: args.file,
    byteOffset: offset,
    fileSize: stat.size,
  };
}
