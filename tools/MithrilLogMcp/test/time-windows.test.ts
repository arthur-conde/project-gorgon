import { describe, it } from 'node:test';
import * as assert from 'node:assert/strict';
import {
  resolveWindow,
  parseInstant,
  PlayerLogTimestamper,
  countPlayerLogCrossings,
} from '../src/util/time-windows.js';

describe('time-windows', () => {
  it('parses ISO 8601 instants', () => {
    const now = new Date('2026-04-26T12:00:00Z');
    const d = parseInstant('2026-04-26T10:00:00Z', now);
    assert.equal(d.toISOString(), '2026-04-26T10:00:00.000Z');
  });

  it('parses relative durations as past instants when negative', () => {
    const now = new Date('2026-04-26T12:00:00Z');
    const d = parseInstant('2h', now, { negative: true });
    assert.equal(d.toISOString(), '2026-04-26T10:00:00.000Z');
  });

  it('resolveWindow defaults until=now when only since is provided', () => {
    const now = new Date('2026-04-26T12:00:00Z');
    const w = resolveWindow(now, { since: '30m' });
    assert.equal(w.since.toISOString(), '2026-04-26T11:30:00.000Z');
    assert.equal(w.until.toISOString(), now.toISOString());
  });

  it('resolveWindow honours an explicit between range', () => {
    const now = new Date('2026-04-26T12:00:00Z');
    const w = resolveWindow(now, {
      between: ['2026-04-25T00:00:00Z', '2026-04-25T23:59:59Z'],
    });
    assert.equal(w.since.toISOString(), '2026-04-25T00:00:00.000Z');
    assert.equal(w.until.toISOString(), '2026-04-25T23:59:59.000Z');
  });

  it('rejects garbage input', () => {
    const now = new Date('2026-04-26T12:00:00Z');
    assert.throws(() => parseInstant('not a time', now));
  });

  it('supports m, h, d units', () => {
    const now = new Date('2026-04-26T12:00:00Z');
    assert.equal(parseInstant('15m', now, { negative: true }).toISOString(), '2026-04-26T11:45:00.000Z');
    assert.equal(parseInstant('1h', now, { negative: true }).toISOString(), '2026-04-26T11:00:00.000Z');
    assert.equal(parseInstant('1d', now, { negative: true }).toISOString(), '2026-04-25T12:00:00.000Z');
  });
});

describe('PlayerLogTimestamper', () => {
  function utcDay(year: number, m1: number, day: number): Date {
    return new Date(Date.UTC(year, m1 - 1, day));
  }

  it('keeps a single ascending sequence on the same day', () => {
    const t = new PlayerLogTimestamper(utcDay(2026, 4, 27));
    const a = t.stamp('[10:00:00] foo', new Date(0));
    const b = t.stamp('[10:00:01] foo', new Date(0));
    const c = t.stamp('[10:00:02] foo', new Date(0));
    assert.equal(a.getUTCDate(), b.getUTCDate());
    assert.equal(b.getUTCDate(), c.getUTCDate());
    assert.ok(a < b && b < c);
  });

  it('advances the date when HH:MM:SS jumps backward (midnight crossing)', () => {
    const t = new PlayerLogTimestamper(utcDay(2026, 4, 26));
    const lastBefore = t.stamp('[23:59:55] foo', new Date(0));
    const firstAfter = t.stamp('[00:00:05] foo', new Date(0));
    // Different calendar dates, and firstAfter > lastBefore.
    assert.ok(firstAfter.getTime() > lastBefore.getTime());
    assert.notEqual(lastBefore.getUTCDate(), firstAfter.getUTCDate());
  });

  it('returns the fallback for lines without a timestamp prefix', () => {
    const t = new PlayerLogTimestamper(utcDay(2026, 4, 27));
    const fallback = new Date('2026-04-27T05:00:00Z');
    const got = t.stamp('Some Unity startup chatter without brackets', fallback);
    assert.equal(got.getTime(), fallback.getTime());
  });

  it('stamps [HH:MM:SS] as a UTC instant regardless of process TZ', () => {
    // Game writes Player.log in UTC. The stamper must produce the same
    // absolute instant whether the process runs in UTC, BST, or EST.
    const t = new PlayerLogTimestamper(utcDay(2026, 4, 27));
    const got = t.stamp('[12:34:56] foo', new Date(0));
    assert.equal(got.toISOString(), '2026-04-27T12:34:56.000Z');
  });
});

describe('countPlayerLogCrossings', () => {
  async function* lines(arr: string[]) {
    for (const line of arr) yield { line };
  }

  it('returns 0 for a single ascending day', async () => {
    const c = await countPlayerLogCrossings(lines([
      '[10:00:00] a', '[10:00:01] b', '[12:00:00] c',
    ]));
    assert.equal(c, 0);
  });

  it('returns 1 for a single midnight crossing', async () => {
    const c = await countPlayerLogCrossings(lines([
      '[20:00:00] a', '[23:59:59] b', '[00:00:01] c', '[01:00:00] d',
    ]));
    assert.equal(c, 1);
  });

  it('returns 2 for two midnight crossings', async () => {
    const c = await countPlayerLogCrossings(lines([
      '[20:00:00] a', '[00:01:00] b', '[20:00:00] c', '[00:01:00] d',
    ]));
    assert.equal(c, 2);
  });

  it('ignores non-timestamped lines', async () => {
    const c = await countPlayerLogCrossings(lines([
      'Unity boot', '[10:00:00] a', 'more boot', '[10:00:01] b',
    ]));
    assert.equal(c, 0);
  });
});

