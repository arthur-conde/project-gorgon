import { describe, it } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { CursorStore, rolloverDetected, snapshotCursor } from '../src/state/cursors.js';

function tmpFile(): string {
  return path.join(os.tmpdir(), `mithril-cursors-${Date.now()}-${Math.random()}.json`);
}

describe('CursorStore', () => {
  it('reads an empty store as no cursors', () => {
    const store = new CursorStore(tmpFile());
    assert.deepEqual(store.list(), []);
  });

  it('persists put/get round-trips', () => {
    const file = tmpFile();
    try {
      const store = new CursorStore(file);
      store.put('debug', 'player', { perFile: { 'Player.log': { byteOffset: 1234, fileSize: 1234, birthtimeMs: 1 } } });

      const reread = new CursorStore(file);
      const state = reread.get('debug', 'player');
      assert.equal(state.perFile['Player.log']?.byteOffset, 1234);
    } finally { fs.unlinkSync(file); }
  });

  it('reset removes a named cursor', () => {
    const file = tmpFile();
    try {
      const store = new CursorStore(file);
      store.put('a', 'player', { perFile: { 'Player.log': { byteOffset: 1, fileSize: 1, birthtimeMs: 1 } } });
      store.reset('a');
      assert.equal(store.list().length, 0);
    } finally { fs.unlinkSync(file); }
  });
});

describe('rolloverDetected', () => {
  it('detects size shrink (truncation)', () => {
    const prev = { byteOffset: 100, fileSize: 100, birthtimeMs: 1 };
    const cur = makeStat({ size: 50, birthtimeMs: 1 });
    assert.equal(rolloverDetected(prev, cur), true);
  });

  it('detects birthtime change (recreate)', () => {
    const prev = { byteOffset: 100, fileSize: 100, birthtimeMs: 1 };
    const cur = makeStat({ size: 200, birthtimeMs: 2 });
    assert.equal(rolloverDetected(prev, cur), true);
  });

  it('returns false on append with stable inode', () => {
    const prev = { byteOffset: 100, fileSize: 100, birthtimeMs: 1 };
    const cur = makeStat({ size: 200, birthtimeMs: 1 });
    assert.equal(rolloverDetected(prev, cur), false);
  });

  it('treats birthtime=0 as no-signal', () => {
    const prev = { byteOffset: 100, fileSize: 100, birthtimeMs: 0 };
    const cur = makeStat({ size: 200, birthtimeMs: 0 });
    assert.equal(rolloverDetected(prev, cur), false);
  });
});

describe('snapshotCursor', () => {
  it('captures size + birthtime at the offset', () => {
    const stat = makeStat({ size: 555, birthtimeMs: 42 });
    const c = snapshotCursor(stat, 100);
    assert.deepEqual(c, { byteOffset: 100, fileSize: 555, birthtimeMs: 42 });
  });
});

function makeStat(over: { size: number; birthtimeMs: number }): fs.Stats {
  // node:fs.Stats is a class, but rolloverDetected only reads the two fields,
  // so an object literal cast is enough for these tests.
  return over as unknown as fs.Stats;
}
