import { describe, it } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { runQueryEvents, QueryEventsInput } from '../src/tools/query-events.js';
import { runAggregate, AggregateInput } from '../src/tools/aggregate.js';
import { runListEventTypes } from '../src/tools/list-event-types.js';
import { CursorStore } from '../src/state/cursors.js';

const stubCursorStore = () => new CursorStore(path.join(os.tmpdir(), `mithril-cursor-stub-${Date.now()}-${Math.random()}.json`));

/**
 * Integration-style: write a small synthetic Player.log to a tmp file, point
 * the server at it via config, run each tool, and assert the shape.
 */

function fixturePath(): string {
  const p = path.join(os.tmpdir(), `mithril-query-fixture-${Date.now()}.log`);
  const lines = [
    '[12:00:00] LocalPlayer: ProcessAddPlayer(-626625832, 290067, "PlayerWolf", "Emraell", "A player!")',
    '[12:00:01] LocalPlayer: ProcessSetPetOwner(11111, 22222)',
    '[12:00:02] Download appearance loop @Carrot(scale=1.0)',
    '[12:00:03] ProcessStartInteraction(900, 1, 100.5, True, "NPC_Marna")',
    '[12:00:04] ProcessDeltaFavor(0, "NPC_Marna", 25.0, True)',
    '[12:00:05] ProcessVendorAddItem(50, RustyDagger(101), True)',
    '[12:00:06] ProcessVendorAddItem(75, RustyDagger(102), True)',
    '[12:00:07] ProcessStartInteraction(901, 1, 200.5, True, "NPC_Velkort")',
  ];
  fs.writeFileSync(p, lines.join('\n') + '\n');
  // Anchor mtime to yesterday so line `[HH:MM:SS]` stamps land safely before
  // `now`, regardless of the time of day or local timezone offset (BST/EST/etc.).
  // PlayerLogTimestamper uses mtime as the date anchor; fixturing into the future
  // would make events appear later than `until: now` during early-UTC test runs.
  const yesterday = new Date(Date.now() - 86_400_000);
  fs.utimesSync(p, yesterday, yesterday);
  return p;
}

describe('runQueryEvents', () => {
  it('returns events that match an event_type filter', async () => {
    const p = fixturePath();
    try {
      const args = QueryEventsInput.parse({
        source: 'player',
        event_type: ['arwen.FavorUpdate'],
        limit: 100,
      });
      const result = await runQueryEvents(args, makeConfig(p), stubCursorStore());
      assert.ok(result.summary.scannedLines >= 8);
      assert.equal(result.events.length, 2);
      assert.ok(result.events.every((e) => e.type === 'arwen.FavorUpdate'));
    } finally { fs.unlinkSync(p); }
  });

  it('honours limit and reports truncated', async () => {
    const p = fixturePath();
    try {
      const args = QueryEventsInput.parse({ source: 'player', limit: 1 });
      const result = await runQueryEvents(args, makeConfig(p), stubCursorStore());
      assert.equal(result.events.length, 1);
      assert.equal(result.summary.truncated, true);
      assert.ok(result.summary.matched > 1);
    } finally { fs.unlinkSync(p); }
  });

  it('honours offset for pagination', async () => {
    const p = fixturePath();
    try {
      const args = QueryEventsInput.parse({
        source: 'player',
        event_type: ['arwen.FavorUpdate'],
        offset: 1,
        limit: 5,
      });
      const result = await runQueryEvents(args, makeConfig(p), stubCursorStore());
      assert.equal(result.events.length, 1);
      assert.equal(result.events[0]?.data.npcKey, 'NPC_Velkort');
    } finally { fs.unlinkSync(p); }
  });

  it('honours filter on data fields', async () => {
    const p = fixturePath();
    try {
      const args = QueryEventsInput.parse({
        source: 'player',
        event_type: ['arwen.FavorUpdate'],
        filter: { npcKey: 'NPC_Marna' },
        limit: 10,
      });
      const result = await runQueryEvents(args, makeConfig(p), stubCursorStore());
      assert.equal(result.events.length, 1);
      assert.equal(result.events[0]?.data.npcKey, 'NPC_Marna');
    } finally { fs.unlinkSync(p); }
  });
});

describe('runAggregate', () => {
  it('count returns total matched events', async () => {
    const p = fixturePath();
    try {
      const args = AggregateInput.parse({ source: 'player', agg: 'count' });
      const result = await runAggregate(args, makeConfig(p), stubCursorStore());
      assert.equal(result.buckets.length, 1);
      assert.equal(result.buckets[0]?.key, 'count');
      // 8 lines, but ProcessStartInteraction matches twice (arwen + smaug).
      // 1 (SetPetOwner) + 1 (AppearanceLoop) + 2 (interaction A: arwen+smaug) +
      // 1 (DeltaFavor) + 1 (VendorAddItem) + 1 (VendorAddItem) +
      // 2 (interaction B: arwen+smaug) + 1 (ProcessAddPlayer) = 10
      assert.equal(result.buckets[0]?.count, 10);
    } finally { fs.unlinkSync(p); }
  });

  it('group_by buckets by event type', async () => {
    const p = fixturePath();
    try {
      const args = AggregateInput.parse({
        source: 'player',
        agg: 'group_by',
        field: 'type',
      });
      const result = await runAggregate(args, makeConfig(p), stubCursorStore());
      const map = new Map(result.buckets.map((b) => [b.key, b.count]));
      assert.equal(map.get('arwen.FavorUpdate'), 2);
      assert.equal(map.get('smaug.NpcInteractionStarted'), 2);
      assert.equal(map.get('smaug.VendorItemSold'), 2);
    } finally { fs.unlinkSync(p); }
  });

  it('top:n returns the N most frequent values', async () => {
    const p = fixturePath();
    try {
      const args = AggregateInput.parse({
        source: 'player',
        agg: 'top',
        field: 'npcKey',
        event_type: ['arwen.FavorUpdate'],
        top_n: 3,
      });
      const result = await runAggregate(args, makeConfig(p), stubCursorStore());
      assert.deepEqual(
        result.buckets.map((b) => b.key).sort(),
        ['NPC_Marna', 'NPC_Velkort'],
      );
    } finally { fs.unlinkSync(p); }
  });
});

describe('runListEventTypes', () => {
  it('returns at least one player event', () => {
    const result = runListEventTypes({ source: 'player' });
    const types = result.types.map((t) => t.type);
    assert.ok(types.includes('arwen.FavorUpdate'));
    assert.ok(types.includes('samwise.SetPetOwner'));
    assert.ok(types.includes('shared.ProcessAddPlayer'));
  });

  it('respects source=chat', () => {
    const result = runListEventTypes({ source: 'chat' });
    assert.ok(result.types.length >= 1);
    assert.ok(result.types.every((t) => t.source === 'chat'));
  });

  it('skips helper-kind entries', () => {
    const result = runListEventTypes({ source: 'all' });
    // The chat-line splitter is a helper; it shouldn't appear as a queryable event type.
    assert.ok(!result.types.some((t) => t.type === ''));
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
