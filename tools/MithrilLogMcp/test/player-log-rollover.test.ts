import { describe, it } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { loadCatalog } from '../src/parsing/catalog.js';
import { scanPlayerLog } from '../src/sources/player-log.js';
import { runQueryEvents, QueryEventsInput } from '../src/tools/query-events.js';
import { CursorStore } from '../src/state/cursors.js';
import type { ParsedEvent } from '../src/parsing/types.js';

/**
 * Player.log rotates when the game starts: the previous run's contents move
 * to Player-prev.log next to the current Player.log. These tests verify the
 * scanner discovers Player-prev.log automatically and reads both files in
 * chronological order.
 */

interface RolloverFixture {
  root: string;
  playerLogPath: string;
  prevLogPath: string;
}

function makeFixture(): RolloverFixture {
  const root = path.join(os.tmpdir(), `mithril-rollover-${Date.now()}-${Math.random()}`);
  fs.mkdirSync(root);
  return {
    root,
    playerLogPath: path.join(root, 'Player.log'),
    prevLogPath: path.join(root, 'Player-prev.log'),
  };
}

function writeLog(p: string, lines: string[], mtime: Date): void {
  fs.writeFileSync(p, lines.join('\n') + '\n');
  fs.utimesSync(p, mtime, mtime);
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

async function collect(gen: AsyncGenerator<ParsedEvent>): Promise<ParsedEvent[]> {
  const out: ParsedEvent[] = [];
  for await (const ev of gen) out.push(ev);
  return out;
}

describe('Player.log rollover discovery', () => {
  it('A: Player-prev.log + Player.log both stream in chronological order', async () => {
    const fix = makeFixture();
    try {
      const twoDaysAgo = new Date(Date.now() - 2 * 86_400_000);
      const yesterday = new Date(Date.now() - 86_400_000);
      writeLog(fix.prevLogPath, [
        '[10:00:01] LocalPlayer: ProcessSetPetOwner(11, 22)',
        '[10:00:02] LocalPlayer: ProcessSetPetOwner(33, 44)',
      ], twoDaysAgo);
      writeLog(fix.playerLogPath, [
        '[10:00:01] LocalPlayer: ProcessSetPetOwner(55, 66)',
        '[10:00:02] LocalPlayer: ProcessSetPetOwner(77, 88)',
      ], yesterday);

      const stats = { scannedBytes: 0, scannedLines: 0, endOffsets: {}, rolledOverFiles: [] };
      const events = await collect(scanPlayerLog(loadCatalog(), {
        path: fix.playerLogPath,
        since: new Date(0),
        until: new Date('2099-01-01'),
      }, stats));

      const ids = events
        .filter((e) => e.type === 'samwise.SetPetOwner')
        .map((e) => e.data.entityId);
      assert.deepEqual(ids, ['11', '33', '55', '77'],
        'expected events from Player-prev.log first, then Player.log');

      // Each file should appear in endOffsets independently.
      assert.ok(fix.prevLogPath in stats.endOffsets);
      assert.ok(fix.playerLogPath in stats.endOffsets);
    } finally {
      fs.rmSync(fix.root, { recursive: true });
    }
  });

  it('B: only Player.log present — no rollover sibling — works as before', async () => {
    const fix = makeFixture();
    try {
      const yesterday = new Date(Date.now() - 86_400_000);
      writeLog(fix.playerLogPath, [
        '[10:00:01] LocalPlayer: ProcessSetPetOwner(11, 22)',
      ], yesterday);

      const stats = { scannedBytes: 0, scannedLines: 0, endOffsets: {}, rolledOverFiles: [] };
      const events = await collect(scanPlayerLog(loadCatalog(), {
        path: fix.playerLogPath,
        since: new Date(0),
        until: new Date('2099-01-01'),
      }, stats));

      const setPetOwners = events.filter((e) => e.type === 'samwise.SetPetOwner');
      assert.equal(setPetOwners.length, 1);
      assert.equal(setPetOwners[0]!.data.entityId, '11');
      assert.deepEqual(Object.keys(stats.endOffsets), [fix.playerLogPath]);
    } finally {
      fs.rmSync(fix.root, { recursive: true });
    }
  });

  it('D: cursor resume — second scan does not re-emit Player-prev.log lines', async () => {
    const fix = makeFixture();
    const cursorFile = tmpCursorFile();
    const store = new CursorStore(cursorFile);
    try {
      // Both mtimes safely in the past relative to `now`, but distinct so
      // discoverPlayerLogPaths sorts them deterministically (prev before
      // current). runQueryEvents clips with `until: now`, so picking
      // mtime = "now - 1h" can flake when the test runs before 10:00 UTC.
      const twoDaysAgo = new Date(Date.now() - 2 * 86_400_000);
      const yesterday = new Date(Date.now() - 86_400_000);
      writeLog(fix.prevLogPath, [
        '[10:00:01] LocalPlayer: ProcessSetPetOwner(11, 22)',
      ], twoDaysAgo);
      writeLog(fix.playerLogPath, [
        '[10:00:01] LocalPlayer: ProcessSetPetOwner(55, 66)',
      ], yesterday);

      const args = QueryEventsInput.parse({
        source: 'player',
        cursor: 'rollover-resume',
        limit: 100,
      });
      const first = await runQueryEvents(args, makeConfig(fix.playerLogPath), store);
      assert.equal(first.events.length, 2,
        `expected 2 events on first call, got ${first.events.length}`);

      // Append a fresh event to Player.log only; keep mtime in the past.
      fs.appendFileSync(fix.playerLogPath,
        '[10:00:02] LocalPlayer: ProcessSetPetOwner(99, 100)\n');
      fs.utimesSync(fix.playerLogPath, yesterday, yesterday);

      const second = await runQueryEvents(args, makeConfig(fix.playerLogPath), store);
      assert.equal(second.events.length, 1,
        `expected only the 1 new event, got ${second.events.length}`);
      assert.equal(second.events[0]!.data.entityId, '99');
    } finally {
      fs.rmSync(fix.root, { recursive: true });
      if (fs.existsSync(cursorFile)) fs.unlinkSync(cursorFile);
    }
  });

  it('F: ProcessAddPlayer in Player-prev.log keeps tagging events in Player.log', async () => {
    const fix = makeFixture();
    try {
      const twoDaysAgo = new Date(Date.now() - 2 * 86_400_000);
      const yesterday = new Date(Date.now() - 86_400_000);
      // ProcessAddPlayer in the old file, regular events in the new.
      writeLog(fix.prevLogPath, [
        '[10:00:01] LocalPlayer: ProcessAddPlayer(-1, 1, "PlayerWolf", "Frodo", "")',
      ], twoDaysAgo);
      writeLog(fix.playerLogPath, [
        '[10:00:01] LocalPlayer: ProcessSetPetOwner(11, 22)',
      ], yesterday);

      const stats = { scannedBytes: 0, scannedLines: 0, endOffsets: {}, rolledOverFiles: [] };
      const events = await collect(scanPlayerLog(loadCatalog(), {
        path: fix.playerLogPath,
        since: new Date(0),
        until: new Date('2099-01-01'),
      }, stats));

      const petEvent = events.find((e) => e.type === 'samwise.SetPetOwner');
      assert.ok(petEvent, 'expected SetPetOwner event');
      assert.equal(petEvent!.activeCharacter, 'Frodo',
        'tracker state from Player-prev.log should carry into Player.log');
    } finally {
      fs.rmSync(fix.root, { recursive: true });
    }
  });
});
