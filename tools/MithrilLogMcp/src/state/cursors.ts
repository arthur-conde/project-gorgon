import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';

/**
 * Persistent named cursors. Each cursor tracks per-source byte offsets so
 * subsequent queries with `cursor: "name"` only see new events. Stored at
 * `%LocalAppData%/MithrilLogMcp/cursors.json` (a separate directory from
 * Mithril's own state — this server is a dev tool, not part of the app).
 *
 * Persistence is a write-temp-then-rename atomic swap, mirroring
 * `Mithril.Shared/Settings/AtomicFile.cs` semantics so a crashed process never
 * leaves a half-written cursors.json behind.
 */

export interface CursorState {
  /** Per-file byte offset for stream-friendly sources (player, chat, mithril). */
  perFile: Record<string, FileCursor>;
}

export interface FileCursor {
  byteOffset: number;
  fileSize: number;
  birthtimeMs: number;
}

interface CursorsFile {
  version: number;
  cursors: Record<string, Record<string, CursorState>>;
}

const CURRENT_VERSION = 1;

export class CursorStore {
  private readonly path: string;
  private cache: CursorsFile | undefined;

  constructor(filePath?: string) {
    this.path = filePath ?? defaultCursorsPath();
  }

  load(): CursorsFile {
    if (this.cache) return this.cache;
    if (!fs.existsSync(this.path)) {
      this.cache = { version: CURRENT_VERSION, cursors: {} };
      return this.cache;
    }
    try {
      const raw = fs.readFileSync(this.path, 'utf8');
      this.cache = JSON.parse(raw) as CursorsFile;
      if (this.cache.version !== CURRENT_VERSION) {
        // Forward-compat path: drop unknown versions and start clean.
        this.cache = { version: CURRENT_VERSION, cursors: {} };
      }
    } catch {
      this.cache = { version: CURRENT_VERSION, cursors: {} };
    }
    return this.cache;
  }

  list(): Array<{ name: string; sources: Record<string, CursorState> }> {
    const doc = this.load();
    return Object.entries(doc.cursors).map(([name, sources]) => ({ name, sources }));
  }

  get(name: string, source: string): CursorState {
    const doc = this.load();
    return doc.cursors[name]?.[source] ?? { perFile: {} };
  }

  put(name: string, source: string, state: CursorState): void {
    const doc = this.load();
    if (!doc.cursors[name]) doc.cursors[name] = {};
    doc.cursors[name]![source] = state;
    this.flush();
  }

  reset(name: string): void {
    const doc = this.load();
    delete doc.cursors[name];
    this.flush();
  }

  private flush(): void {
    if (!this.cache) return;
    fs.mkdirSync(path.dirname(this.path), { recursive: true });
    const tmp = `${this.path}.partial`;
    fs.writeFileSync(tmp, JSON.stringify(this.cache, null, 2), 'utf8');
    fs.renameSync(tmp, this.path);
  }
}

/**
 * Decides whether a previously-recorded cursor entry is still valid for a
 * given file. Mirrors the rollover heuristics in Mithril's
 * `PlayerLogTailReader.ReadNew`:
 *  - file shrunk → rotate, restart from 0
 *  - inode/birthtime changed → file deleted+recreated, restart from 0
 *  - size unchanged → fine, stay where we are
 */
export function rolloverDetected(prev: FileCursor, current: fs.Stats): boolean {
  if (current.size < prev.byteOffset) return true;
  if (current.size < prev.fileSize) return true;
  // birthtimeMs is 0 on some FS — treat as no signal in that case.
  if (prev.birthtimeMs > 0 && current.birthtimeMs > 0 && current.birthtimeMs !== prev.birthtimeMs) return true;
  return false;
}

export function snapshotCursor(stat: fs.Stats, byteOffset: number): FileCursor {
  return {
    byteOffset,
    fileSize: stat.size,
    birthtimeMs: stat.birthtimeMs,
  };
}

function defaultCursorsPath(): string {
  if (process.env.MITHRIL_LOG_MCP_CURSORS) return process.env.MITHRIL_LOG_MCP_CURSORS;
  const localApp = process.env.LOCALAPPDATA
    ?? path.join(os.homedir(), 'AppData', 'Local');
  return path.join(localApp, 'MithrilLogMcp', 'cursors.json');
}
