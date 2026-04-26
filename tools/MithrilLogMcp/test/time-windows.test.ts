import { describe, it } from 'node:test';
import * as assert from 'node:assert/strict';
import { resolveWindow, parseInstant } from '../src/util/time-windows.js';

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
