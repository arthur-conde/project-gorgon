import * as fs from 'node:fs';
import { fileURLToPath } from 'node:url';
import { z } from 'zod';
import type { ServerConfig } from '../config.js';
import { discoverPlayerLogPaths } from '../sources/player-log-discovery.js';

export const ServerInfoInput = z.object({});

export type ServerInfoArgs = z.infer<typeof ServerInfoInput>;

export interface ServerInfoMetadata {
  name: string;
  version: string;
}

export function runServerInfo(
  _args: ServerInfoArgs,
  meta: ServerInfoMetadata,
  config: ServerConfig,
) {
  return {
    server: {
      name: meta.name,
      version: meta.version,
    },
    build: resolveBuildInfo(),
    config: {
      playerLogPath: config.playerLogPath,
      chatLogDir: config.chatLogDir,
      mithrilLogDir: config.mithrilLogDir,
      characterRoot: config.characterRoot,
      shellSettingsPath: config.shellSettingsPath,
    },
    playerLogFiles: discoverPlayerLogPaths(config.playerLogPath).map((f) => ({
      path: f.path,
      sizeBytes: f.stat.size,
      mtime: f.stat.mtime.toISOString(),
    })),
  };
}

// Read the on-disk mtime of the compiled JS for this module — that's the
// "when was this server last built" signal that's actually useful for an
// in-tree (non-released) MCP server. The version constant is hand-bumped and
// usually lies; the file mtime doesn't.
function resolveBuildInfo(): { sourcePath: string; builtAt: string | null } {
  let sourcePath: string;
  try {
    sourcePath = fileURLToPath(import.meta.url);
  } catch {
    return { sourcePath: '<unknown>', builtAt: null };
  }
  try {
    const stat = fs.statSync(sourcePath);
    return { sourcePath, builtAt: stat.mtime.toISOString() };
  } catch {
    return { sourcePath, builtAt: null };
  }
}
