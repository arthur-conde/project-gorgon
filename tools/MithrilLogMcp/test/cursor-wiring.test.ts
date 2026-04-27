import { describe, it } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { runQueryEvents, QueryEventsInput } from '../src/tools/query-events.js';
import { CursorStore } from '../src/state/cursors.js';

/**
 * Multi-turn cursor flow: a query advances the cursor; a follow-up query
 * with the same cursor name skips the events from turn 1 and only sees what
 * was appended afterwards. Verifies the v0.4 wiring end-to-end.
 */

function newPlayerLog(): { dir: string; path: string } {
  const dir = path.join(os.tmpdir(), `mithril-cursor-${Date.now()}-${Math.random()}`);
  fs.mkdirSync(dir);
  const p = path.join(dir, 'Player.log');
  fs.writeFileSync(p,
    [
      '[12:00:01] LocalPlayer: ProcessSetPetOwner(11111, 22222)',
      '[12:00:02] LocalPlayer: ProcessSetPetOwner(33333, 44444)',
    ].join('\n') + '\n');
  // Past mtime keeps line `[HH:MM:SS]` stamps safely before `now` — see the
  // matching note in query-events.test.ts.
  const yesterday = new Date(Date.now() - 86_400_000);
  fs.utimesSync(p, yesterday, yesterday);
  return { dir, path: p };
}

function append(p: string, lines: string[]): void {
  fs.appendFileSync(p, lines.join('\n') + '\n');
  const yesterday = new Date(Date.now() - 86_400_000);
  fs.utimesSync(p, yesterday, yesterday);
}

function makeConfig(playerLogPath: string) {
  return {
    playerLogPath,
    chatLogDir: '',
    mithrilLogDir: '',
    characterRoot: '',
    shellSettingsPath: '',
  };
}

function tmpCursorFile(): string {
  return path.join(os.tmpdir(), `mithril-cursors-${Date.now()}-${Math.random()}.json`);
}

describe('query_events cursor wiring', () => {
  it('first call sees all events, second call sees only new ones', async () => {
    const { dir, path: playerPath } = newPlayerLog();
    const cursorFile = tmpCursorFile();
    const store = new CursorStore(cursorFile);
    try {
      const args = QueryEventsInput.parse({
        source: 'player',
        cursor: 'investigation-1',
        limit: 100,
      });
      const first = await runQueryEvents(args, makeConfig(playerPath), store);
      assert.equal(first.events.length, 2,
        `expected 2 events on first call, got ${first.events.length}`);
      assert.ok(first.summary.cursor, 'expected cursor info in summary');

      // Append two more events; the cursor should restrict the next call to those.
      append(playerPath, [
        '[12:00:05] LocalPlayer: ProcessSetPetOwner(55555, 66666)',
        '[12:00:06] LocalPlayer: ProcessSetPetOwner(77777, 88888)',
      ]);

      const second = await runQueryEvents(args, makeConfig(playerPath), store);
      assert.equal(second.events.length, 2,
        `expected 2 *new* events on second call, got ${second.events.length}`);
      const ids = second.events.map((e) => e.data.entityId);
      assert.deepEqual(ids, ['55555', '77777']);
    } finally {
      fs.rmSync(dir, { recursive: true });
      if (fs.existsSync(cursorFile)) fs.unlinkSync(cursorFile);
    }
  });

  it('cursor reset returns to scanning from the start', async () => {
    const { dir, path: playerPath } = newPlayerLog();
    const cursorFile = tmpCursorFile();
    const store = new CursorStore(cursorFile);
    try {
      const args = QueryEventsInput.parse({
        source: 'player',
        cursor: 'investigation-2',
        limit: 100,
      });
      const first = await runQueryEvents(args, makeConfig(playerPath), store);
      assert.equal(first.events.length, 2);

      store.reset('investigation-2');

      const replay = await runQueryEvents(args, makeConfig(playerPath), store);
      assert.equal(replay.events.length, 2,
        'after reset, the cursor should re-scan from byte 0');
    } finally {
      fs.rmSync(dir, { recursive: true });
      if (fs.existsSync(cursorFile)) fs.unlinkSync(cursorFile);
    }
  });

  it('detects truncation and re-reads from byte 0', async () => {
    const { dir, path: playerPath } = newPlayerLog();
    const cursorFile = tmpCursorFile();
    const store = new CursorStore(cursorFile);
    try {
      const args = QueryEventsInput.parse({
        source: 'player',
        cursor: 'investigation-3',
        limit: 100,
      });
      const first = await runQueryEvents(args, makeConfig(playerPath), store);
      assert.equal(first.events.length, 2);

      // Truncate + write a single new line. Size shrinks vs the cursor's recorded
      // fileSize, so rolloverDetected returns true and we read from 0.
      fs.writeFileSync(playerPath, '[12:00:09] LocalPlayer: ProcessSetPetOwner(99999, 0)\n');
      const past = new Date(Date.now() - 86_400_000);
      fs.utimesSync(playerPath, past, past);

      const after = await runQueryEvents(args, makeConfig(playerPath), store);
      assert.equal(after.events.length, 1);
      assert.deepEqual(after.summary.cursor?.rolledOverFiles, [playerPath]);
    } finally {
      fs.rmSync(dir, { recursive: true });
      if (fs.existsSync(cursorFile)) fs.unlinkSync(cursorFile);
    }
  });

  it('cursor and time window are independent — both apply', async () => {
    const { dir, path: playerPath } = newPlayerLog();
    const cursorFile = tmpCursorFile();
    const store = new CursorStore(cursorFile);
    try {
      // First call advances the cursor to end-of-file.
      const seed = QueryEventsInput.parse({
        source: 'player',
        cursor: 'investigation-4',
        limit: 100,
      });
      await runQueryEvents(seed, makeConfig(playerPath), store);

      append(playerPath, [
        '[12:00:05] LocalPlayer: ProcessSetPetOwner(55555, 0)',
      ]);

      // Cursor keeps us past the original two lines, AND `between` further
      // restricts to a sub-range — both filters compose.
      const args = QueryEventsInput.parse({
        source: 'player',
        cursor: 'investigation-4',
        between: ['1970-01-01T00:00:00Z', '2099-01-01T00:00:00Z'],
        limit: 100,
      });
      const result = await runQueryEvents(args, makeConfig(playerPath), store);
      assert.equal(result.events.length, 1);
      assert.equal(result.events[0]?.data.entityId, '55555');
    } finally {
      fs.rmSync(dir, { recursive: true });
      if (fs.existsSync(cursorFile)) fs.unlinkSync(cursorFile);
    }
  });
});
