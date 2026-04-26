import * as os from 'node:os';
import * as path from 'node:path';

/**
 * Resolves on-disk paths the server reads. Each can be overridden via env var
 * for testing (or for unusual installs).
 *
 * Defaults match Mithril's hard-coded paths in `GameLocator.cs` and
 * `Mithril.Shell/Program.cs` — keep them aligned when those move.
 */
export interface ServerConfig {
  playerLogPath: string;
  chatLogDir: string;
  mithrilLogDir: string;
  characterRoot: string;
  shellSettingsPath: string;
}

export function loadConfig(): ServerConfig {
  const localApp = process.env.LOCALAPPDATA
    ?? path.join(os.homedir(), 'AppData', 'Local');
  const localAppLow = path.join(localApp + 'Low');

  const gameRoot = process.env.MITHRIL_GAME_ROOT
    ?? path.join(localAppLow, 'Elder Game', 'Project Gorgon');

  return {
    playerLogPath: process.env.MITHRIL_PLAYER_LOG
      ?? path.join(gameRoot, 'Player.log'),
    chatLogDir: process.env.MITHRIL_CHAT_LOG_DIR
      ?? path.join(gameRoot, 'ChatLogs'),
    mithrilLogDir: process.env.MITHRIL_DIAGNOSTIC_LOG_DIR
      ?? path.join(localApp, 'Mithril', 'Shell', 'logs'),
    characterRoot: process.env.MITHRIL_CHARACTER_ROOT
      ?? path.join(localApp, 'Mithril', 'characters'),
    shellSettingsPath: process.env.MITHRIL_SHELL_SETTINGS
      ?? path.join(localApp, 'Mithril', 'Shell', 'shell.json'),
  };
}
