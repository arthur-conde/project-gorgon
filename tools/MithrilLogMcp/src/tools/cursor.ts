import { z } from 'zod';
import { CursorStore } from '../state/cursors.js';

export const CursorListInput = z.object({});
export const CursorResetInput = z.object({
  name: z.string().min(1),
});

export type CursorListArgs = z.infer<typeof CursorListInput>;
export type CursorResetArgs = z.infer<typeof CursorResetInput>;

export function runCursorList(_args: CursorListArgs, store: CursorStore) {
  const cursors = store.list().map((c) => ({
    name: c.name,
    sources: Object.fromEntries(
      Object.entries(c.sources).map(([source, state]) => [
        source,
        { fileCount: Object.keys(state.perFile).length },
      ]),
    ),
  }));
  return { cursors };
}

export function runCursorReset(args: CursorResetArgs, store: CursorStore) {
  store.reset(args.name);
  return { ok: true, name: args.name };
}
