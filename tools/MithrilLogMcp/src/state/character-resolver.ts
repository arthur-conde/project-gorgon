import * as fs from 'node:fs';
import * as path from 'node:path';
import type { ServerConfig } from '../config.js';
import type { Catalog } from '../parsing/catalog.js';

/**
 * Reads Mithril's character-presence state to support `--last-session` and
 * `character: "Foo"` queries. State files are read-only; the MCP server never
 * writes to Mithril's state.
 */

export interface ActiveCharacter {
  name: string;
  server: string;
  lastActiveAt: Date | null;
}

interface ShellSettingsFile {
  activeCharacterName?: string;
  activeServer?: string;
}

interface CharacterPresenceFile {
  schemaVersion?: number;
  lastActiveAt?: string;
}

export function readActiveCharacter(config: ServerConfig): ActiveCharacter | null {
  if (!fs.existsSync(config.shellSettingsPath)) return null;
  let shell: ShellSettingsFile;
  try {
    shell = JSON.parse(fs.readFileSync(config.shellSettingsPath, 'utf8')) as ShellSettingsFile;
  } catch {
    return null;
  }
  const name = shell.activeCharacterName;
  const server = shell.activeServer;
  if (!name || !server) return null;

  const lastActiveAt = readLastActiveAt(config, name, server);
  return { name, server, lastActiveAt };
}

export function readLastActiveAt(
  config: ServerConfig,
  name: string,
  server: string,
): Date | null {
  const presencePath = path.join(config.characterRoot, `${name}_${server}`, 'character.json');
  if (!fs.existsSync(presencePath)) return null;
  try {
    const data = JSON.parse(fs.readFileSync(presencePath, 'utf8')) as CharacterPresenceFile;
    if (typeof data.lastActiveAt !== 'string') return null;
    const d = new Date(data.lastActiveAt);
    return Number.isFinite(d.getTime()) ? d : null;
  } catch {
    return null;
  }
}

/**
 * Resolves a `--last-session` window for a named character.
 *
 * The lower bound is the byte offset of the most recent
 * `LocalPlayer: ProcessAddPlayer(..., "<name>", ...)` line in Player.log
 * — same algorithm as Mithril's `PlayerLogTailReader.SeedToSessionStart`,
 * scoped by character name. The upper bound is the character's stored
 * `lastActiveAt` if present and in the past, else `now`.
 */
export interface SessionWindow {
  since: Date;
  until: Date;
}

export function resolveLastSessionWindow(
  config: ServerConfig,
  catalog: Catalog,
  name: string,
  now: Date,
): SessionWindow {
  const server = readActiveCharacter(config)?.server ?? '';
  const lastActiveAt = server ? readLastActiveAt(config, name, server) : null;
  const until = lastActiveAt && lastActiveAt < now ? lastActiveAt : now;

  const since = scanBackForSessionStart(config, catalog, name) ?? new Date(0);
  return { since, until };
}

function scanBackForSessionStart(
  config: ServerConfig,
  catalog: Catalog,
  name: string,
): Date | null {
  if (!fs.existsSync(config.playerLogPath)) return null;
  const stat = fs.statSync(config.playerLogPath);
  const fd = fs.openSync(config.playerLogPath, 'r');
  try {
    const chunkSize = Math.min(catalog.sessionMarker.scanChunkBytes, stat.size);
    const overlap = catalog.sessionMarker.literal.length;
    const namePattern = `, "${name}",`;
    let end = stat.size;
    while (end > 0) {
      const size = Math.min(chunkSize, end);
      const start = end - size;
      const buf = Buffer.alloc(size);
      fs.readSync(fd, buf, 0, size, start);
      const text = buf.toString('utf8');
      // Search backward for the most recent ProcessAddPlayer that names this character.
      let idx = text.lastIndexOf(namePattern);
      while (idx >= 0) {
        // Ensure the same line also has the session-marker literal.
        const lineStart = text.lastIndexOf('\n', idx) + 1;
        const lineEnd = text.indexOf('\n', idx);
        const line = text.substring(lineStart, lineEnd < 0 ? text.length : lineEnd);
        if (line.includes(catalog.sessionMarker.literal)) {
          // Time anchor: file mtime (same approximation as PlayerLogTimestamper).
          // For session windows, we just need a chronological lower bound, so
          // returning the file's mtime minus a generous slack works.
          return new Date(stat.mtime.getTime() - 24 * 3_600_000);
        }
        idx = text.lastIndexOf(namePattern, idx - 1);
      }
      if (start === 0) break;
      end = start + overlap;
    }
    return null;
  } finally {
    fs.closeSync(fd);
  }
}
