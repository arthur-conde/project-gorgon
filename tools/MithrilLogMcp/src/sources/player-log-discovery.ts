import * as fs from 'node:fs';
import * as path from 'node:path';

/**
 * Player.log gets rotated between sessions: when the game starts, the
 * previous run's contents move to `Player-prev.log` in the same directory.
 * The MCP scanner used to only open the current Player.log, leaving every
 * pre-rotation event invisible to `since: 7d` windows. {@link discoverPlayerLogPaths}
 * returns both files (when present) in oldest-first order so the scanner
 * can stream them as one logical timeline.
 *
 * `oldPlayer.log` is intentionally NOT probed — it isn't produced by the
 * game's own rotation, only by manual renames during debugging.
 */

export interface DiscoveredPlayerLog {
  path: string;
  stat: fs.Stats;
}

/**
 * Returns existing Player.log files (current + previous rotation) sorted
 * by mtime ascending.
 *
 * No cross-file dedupe: the two probed paths are distinct by construction,
 * and game rotation never produces two paths with overlapping bytes (a
 * rename moves all bytes, leaving the source empty). Per-file cursor
 * invalidation via `rolloverDetected` already handles the
 * file-identity-changed case at its own layer.
 */
export function discoverPlayerLogPaths(currentPath: string): DiscoveredPlayerLog[] {
  const dir = path.dirname(currentPath);
  const candidates = [
    path.join(dir, 'Player-prev.log'),
    currentPath,
  ];

  const found: DiscoveredPlayerLog[] = [];
  for (const candidate of candidates) {
    if (!fs.existsSync(candidate)) continue;
    found.push({ path: candidate, stat: fs.statSync(candidate) });
  }

  found.sort((a, b) => a.stat.mtimeMs - b.stat.mtimeMs);
  return found;
}
