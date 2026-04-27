import { describe, it } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { ActiveCharacterTracker } from '../src/parsing/active-character-tracker.js';
import { findActiveCharacterAt } from '../src/state/character-resolver.js';
import { loadCatalog } from '../src/parsing/catalog.js';
import { scanPlayerLog } from '../src/sources/player-log.js';
import { runQueryEvents, QueryEventsInput } from '../src/tools/query-events.js';
import { CursorStore } from '../src/state/cursors.js';
import type { ParsedEvent } from '../src/parsing/types.js';

const T = new Date(Date.UTC(2026, 3, 27, 12, 0, 0));

function syntheticEvent(over: Partial<ParsedEvent>): ParsedEvent {
  return {
    type: '',
    ts: T.toISOString(),
    module: '',
    source: 'player',
    file: 'Player.log',
    line: 1,
    byteOffset: 0,
    data: {},
    ...over,
  };
}

describe('ActiveCharacterTracker', () => {
  it('updates on shared.ProcessAddPlayer events', () => {
    const t = new ActiveCharacterTracker();
    assert.equal(t.active, undefined);
    t.observe(syntheticEvent({ type: 'shared.ProcessAddPlayer', data: { characterName: 'Emraell' } }));
    assert.equal(t.active, 'Emraell');
    t.observe(syntheticEvent({ type: 'shared.ProcessAddPlayer', data: { characterName: 'Velkort' } }));
    assert.equal(t.active, 'Velkort');
  });

  it('ignores non-ProcessAddPlayer events', () => {
    const t = new ActiveCharacterTracker('Emraell');
    t.observe(syntheticEvent({ type: 'samwise.SetPetOwner', data: { entityId: '12345' } }));
    assert.equal(t.active, 'Emraell');
  });

  it('honours an initial value', () => {
    const t = new ActiveCharacterTracker('Emraell');
    assert.equal(t.active, 'Emraell');
  });
});

function newPlayerLog(lines: string[]): { dir: string; path: string } {
  const dir = path.join(os.tmpdir(), `mithril-active-${Date.now()}-${Math.random()}`);
  fs.mkdirSync(dir);
  const p = path.join(dir, 'Player.log');
  fs.writeFileSync(p, lines.join('\n') + '\n');
  // Past mtime keeps line `[HH:MM:SS]` stamps before `now` regardless of TZ.
  const yesterday = new Date(Date.now() - 86_400_000);
  fs.utimesSync(p, yesterday, yesterday);
  return { dir, path: p };
}

describe('findActiveCharacterAt', () => {
  it('returns the most recent ProcessAddPlayer character before a byte offset', () => {
    const { dir, path: p } = newPlayerLog([
      '[12:00:00] LocalPlayer: ProcessAddPlayer(-1, 1, "PlayerWolf", "Emraell", "")',
      '[12:01:00] LocalPlayer: ProcessSetPetOwner(11, 22)',
      '[12:02:00] LocalPlayer: ProcessAddPlayer(-2, 2, "PlayerWolf", "Velkort", "")',
      '[12:03:00] LocalPlayer: ProcessSetPetOwner(33, 44)',
    ]);
    try {
      const catalog = loadCatalog();
      const stat = fs.statSync(p);
      assert.equal(findActiveCharacterAt(p, stat.size, catalog), 'Velkort');
    } finally { fs.rmSync(dir, { recursive: true }); }
  });

  it('returns null when no ProcessAddPlayer exists before the offset', () => {
    const { dir, path: p } = newPlayerLog([
      '[12:00:00] Some unrelated startup chatter',
      '[12:00:01] LocalPlayer: ProcessSetPetOwner(11, 22)',
    ]);
    try {
      const catalog = loadCatalog();
      const stat = fs.statSync(p);
      assert.equal(findActiveCharacterAt(p, stat.size, catalog), null);
    } finally { fs.rmSync(dir, { recursive: true }); }
  });

  it('returns null for offset 0 (no bytes to scan)', () => {
    const { dir, path: p } = newPlayerLog([
      '[12:00:00] LocalPlayer: ProcessAddPlayer(-1, 1, "PlayerWolf", "Emraell", "")',
    ]);
    try {
      const catalog = loadCatalog();
      assert.equal(findActiveCharacterAt(p, 0, catalog), null);
    } finally { fs.rmSync(dir, { recursive: true }); }
  });
});

describe('scanPlayerLog stamps activeCharacter', () => {
  it('stamps every event after a ProcessAddPlayer with the active character', async () => {
    const { dir, path: p } = newPlayerLog([
      '[12:00:00] LocalPlayer: ProcessAddPlayer(-1, 1, "PlayerWolf", "Emraell", "")',
      '[12:00:01] LocalPlayer: ProcessSetPetOwner(11, 22)',
      '[12:00:02] LocalPlayer: ProcessSetPetOwner(33, 44)',
    ]);
    try {
      const catalog = loadCatalog();
      const stats = { scannedBytes: 0, scannedLines: 0, endOffsets: {}, rolledOverFiles: [] };
      const events: ParsedEvent[] = [];
      for await (const e of scanPlayerLog(catalog, {
        path: p, since: new Date(0), until: new Date('2099-01-01'),
      }, stats)) events.push(e);

      // At least 1 ProcessAddPlayer + 2 SetPetOwner. Each should be stamped.
      const stamped = events.filter((e) => e.activeCharacter === 'Emraell');
      assert.ok(stamped.length >= 3, `expected >=3 stamped events, got ${stamped.length}`);
      const setPetOwners = events.filter((e) => e.type === 'samwise.SetPetOwner');
      assert.ok(setPetOwners.every((e) => e.activeCharacter === 'Emraell'));
    } finally { fs.rmSync(dir, { recursive: true }); }
  });

  it('leaves activeCharacter undefined for events before the first ProcessAddPlayer', async () => {
    // Pet owner before any login — pre-game events.
    const { dir, path: p } = newPlayerLog([
      '[11:59:00] LocalPlayer: ProcessSetPetOwner(11, 22)',
      '[12:00:00] LocalPlayer: ProcessAddPlayer(-1, 1, "PlayerWolf", "Emraell", "")',
      '[12:00:01] LocalPlayer: ProcessSetPetOwner(33, 44)',
    ]);
    try {
      const catalog = loadCatalog();
      const stats = { scannedBytes: 0, scannedLines: 0, endOffsets: {}, rolledOverFiles: [] };
      const events: ParsedEvent[] = [];
      for await (const e of scanPlayerLog(catalog, {
        path: p, since: new Date(0), until: new Date('2099-01-01'),
      }, stats)) events.push(e);

      const sortedSetPetOwners = events
        .filter((e) => e.type === 'samwise.SetPetOwner')
        .sort((a, b) => a.byteOffset - b.byteOffset);
      assert.equal(sortedSetPetOwners.length, 2);
      assert.equal(sortedSetPetOwners[0]!.activeCharacter, undefined,
        'pre-login pet owner should not be stamped');
      assert.equal(sortedSetPetOwners[1]!.activeCharacter, 'Emraell');
    } finally { fs.rmSync(dir, { recursive: true }); }
  });
});

describe('character: filter on player events', () => {
  it('returns only events stamped with the requested character', async () => {
    const { dir, path: p } = newPlayerLog([
      '[12:00:00] LocalPlayer: ProcessAddPlayer(-1, 1, "PlayerWolf", "Emraell", "")',
      '[12:00:01] LocalPlayer: ProcessSetPetOwner(11, 22)',
      '[12:00:02] LocalPlayer: ProcessAddPlayer(-2, 2, "PlayerWolf", "Velkort", "")',
      '[12:00:03] LocalPlayer: ProcessSetPetOwner(33, 44)',
      '[12:00:04] LocalPlayer: ProcessSetPetOwner(55, 66)',
    ]);
    const cursorFile = path.join(os.tmpdir(), `mithril-cursor-${Date.now()}.json`);
    try {
      const args = QueryEventsInput.parse({
        source: 'player',
        character: 'Velkort',
        event_type: ['samwise.SetPetOwner'],
        limit: 100,
      });
      const result = await runQueryEvents(args, makeConfig(p), new CursorStore(cursorFile));
      assert.equal(result.events.length, 2,
        'two SetPetOwner events were emitted while Velkort was active');
      assert.ok(result.events.every((e) => e.activeCharacter === 'Velkort'));
    } finally {
      fs.rmSync(dir, { recursive: true });
      if (fs.existsSync(cursorFile)) fs.unlinkSync(cursorFile);
    }
  });

  it('skips player events emitted before any ProcessAddPlayer when filter is set', async () => {
    const { dir, path: p } = newPlayerLog([
      '[11:59:00] LocalPlayer: ProcessSetPetOwner(11, 22)',
      '[12:00:00] LocalPlayer: ProcessAddPlayer(-1, 1, "PlayerWolf", "Emraell", "")',
      '[12:00:01] LocalPlayer: ProcessSetPetOwner(33, 44)',
    ]);
    const cursorFile = path.join(os.tmpdir(), `mithril-cursor-${Date.now()}.json`);
    try {
      const args = QueryEventsInput.parse({
        source: 'player',
        character: 'Emraell',
        event_type: ['samwise.SetPetOwner'],
        limit: 100,
      });
      const result = await runQueryEvents(args, makeConfig(p), new CursorStore(cursorFile));
      assert.equal(result.events.length, 1,
        'pre-login pet owner is dropped because activeCharacter is unknown');
    } finally {
      fs.rmSync(dir, { recursive: true });
      if (fs.existsSync(cursorFile)) fs.unlinkSync(cursorFile);
    }
  });

  it('cursor resume backfills the active character', async () => {
    const { dir, path: p } = newPlayerLog([
      '[12:00:00] LocalPlayer: ProcessAddPlayer(-1, 1, "PlayerWolf", "Emraell", "")',
      '[12:00:01] LocalPlayer: ProcessSetPetOwner(11, 22)',
    ]);
    const cursorFile = path.join(os.tmpdir(), `mithril-cursor-${Date.now()}.json`);
    try {
      // First call seeds the cursor at end-of-file.
      const seed = QueryEventsInput.parse({
        source: 'player', cursor: 'session-A', limit: 100,
      });
      await runQueryEvents(seed, makeConfig(p), new CursorStore(cursorFile));

      // Append more SetPetOwner events without another ProcessAddPlayer.
      // Without backfill, the tracker would have no active character and
      // would skip these under a `character` filter.
      fs.appendFileSync(p,
        '[12:00:05] LocalPlayer: ProcessSetPetOwner(33, 44)\n' +
        '[12:00:06] LocalPlayer: ProcessSetPetOwner(55, 66)\n');
      const past = new Date(Date.now() - 86_400_000);
      fs.utimesSync(p, past, past);

      const args = QueryEventsInput.parse({
        source: 'player',
        cursor: 'session-A',
        character: 'Emraell',
        event_type: ['samwise.SetPetOwner'],
        limit: 100,
      });
      const result = await runQueryEvents(args, makeConfig(p), new CursorStore(cursorFile));
      assert.equal(result.events.length, 2,
        'backfill should let the filter match new events even though their session ' +
        'started before the cursor');
      assert.ok(result.events.every((e) => e.activeCharacter === 'Emraell'));
    } finally {
      fs.rmSync(dir, { recursive: true });
      if (fs.existsSync(cursorFile)) fs.unlinkSync(cursorFile);
    }
  });
});

function makeConfig(playerLogPath: string) {
  return {
    playerLogPath,
    chatLogDir: '',
    mithrilLogDir: '',
    characterRoot: '',
    shellSettingsPath: '',
  };
}
