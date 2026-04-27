import { describe, it } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { resolveWindow } from '../src/util/time-windows.js';
import { runQueryEvents, QueryEventsInput } from '../src/tools/query-events.js';
import { runAggregate, AggregateInput } from '../src/tools/aggregate.js';
import { runCursorSet, runCursorList, CursorSetInput } from '../src/tools/cursor.js';
import { CursorStore } from '../src/state/cursors.js';

function newPlayerLog(lines: string[]): { dir: string; path: string } {
  const dir = path.join(os.tmpdir(), `mithril-polish-${Date.now()}-${Math.random()}`);
  fs.mkdirSync(dir);
  const p = path.join(dir, 'Player.log');
  fs.writeFileSync(p, lines.join('\n') + '\n');
  const yesterday = new Date(Date.now() - 86_400_000);
  fs.utimesSync(p, yesterday, yesterday);
  return { dir, path: p };
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
  return path.join(os.tmpdir(), `mithril-polish-cursors-${Date.now()}-${Math.random()}.json`);
}

describe('resolveWindow validates ordering', () => {
  it('throws when between is reversed', () => {
    const now = new Date('2026-04-27T12:00:00Z');
    assert.throws(
      () => resolveWindow(now, { between: ['2099-01-01', '1970-01-01'] }),
      /reversed/i,
    );
  });

  it('throws when since resolves after until', () => {
    const now = new Date('2026-04-27T12:00:00Z');
    assert.throws(
      () => resolveWindow(now, { since: '2099-01-01T00:00:00Z', until: '2020-01-01T00:00:00Z' }),
      /after/i,
    );
  });

  it('accepts a valid forward range', () => {
    const now = new Date('2026-04-27T12:00:00Z');
    const w = resolveWindow(now, { between: ['2026-04-27T00:00:00Z', '2026-04-27T11:59:59Z'] });
    assert.equal(w.since.toISOString(), '2026-04-27T00:00:00.000Z');
  });
});

describe('aggregate with cursor', () => {
  it('advances the cursor after a successful aggregate call', async () => {
    const { dir, path: p } = newPlayerLog([
      '[12:00:00] LocalPlayer: ProcessAddPlayer(-1, 1, "PlayerWolf", "Emraell", "")',
      '[12:00:01] LocalPlayer: ProcessSetPetOwner(11, 22)',
      '[12:00:02] LocalPlayer: ProcessSetPetOwner(33, 44)',
    ]);
    const cursorFile = tmpCursorFile();
    const store = new CursorStore(cursorFile);
    try {
      const args = AggregateInput.parse({
        source: 'player',
        agg: 'count',
        cursor: 'agg-1',
      });
      const first = await runAggregate(args, makeConfig(p), store);
      assert.ok(first.summary.matched > 0, 'first call should see all events');
      assert.ok(first.summary.cursor, 'cursor info should appear in summary');

      // Append more lines, then re-aggregate with the same cursor.
      fs.appendFileSync(p, '[12:00:05] LocalPlayer: ProcessSetPetOwner(55, 66)\n');
      const past = new Date(Date.now() - 86_400_000);
      fs.utimesSync(p, past, past);

      const second = await runAggregate(args, makeConfig(p), store);
      assert.equal(second.summary.matched, 1,
        'cursor restricted second call to the appended event');
    } finally {
      fs.rmSync(dir, { recursive: true });
      if (fs.existsSync(cursorFile)) fs.unlinkSync(cursorFile);
    }
  });
});

describe('cursor_set', () => {
  it('anchor=end fast-forwards past existing content', async () => {
    const { dir, path: p } = newPlayerLog([
      '[12:00:00] LocalPlayer: ProcessAddPlayer(-1, 1, "PlayerWolf", "Emraell", "")',
      '[12:00:01] LocalPlayer: ProcessSetPetOwner(11, 22)',
    ]);
    const cursorFile = tmpCursorFile();
    const store = new CursorStore(cursorFile);
    try {
      const setArgs = CursorSetInput.parse({
        name: 'forward-cursor',
        source: 'player',
        file: p,
        anchor: 'end',
      });
      const setResult = runCursorSet(setArgs, store);
      assert.equal(setResult.byteOffset, fs.statSync(p).size);

      // Append a new event. With anchor=end set previously, query should
      // only see the new line — nothing from before.
      fs.appendFileSync(p, '[12:00:05] LocalPlayer: ProcessSetPetOwner(99, 0)\n');
      const past = new Date(Date.now() - 86_400_000);
      fs.utimesSync(p, past, past);

      const queryArgs = QueryEventsInput.parse({
        source: 'player',
        cursor: 'forward-cursor',
        limit: 10,
      });
      const result = await runQueryEvents(queryArgs, makeConfig(p), store);
      assert.equal(result.events.length, 1,
        'only the appended event should be visible');
      assert.equal(result.events[0]?.data.entityId, '99');
    } finally {
      fs.rmSync(dir, { recursive: true });
      if (fs.existsSync(cursorFile)) fs.unlinkSync(cursorFile);
    }
  });

  it('anchor=offset rejects out-of-range values', async () => {
    const { dir, path: p } = newPlayerLog(['just one line\n']);
    const store = new CursorStore(tmpCursorFile());
    try {
      assert.throws(
        () => runCursorSet({
          name: 'oob',
          source: 'player',
          file: p,
          anchor: 'offset',
          byteOffset: 1_000_000_000,
        }, store),
        /exceeds file size/,
      );
    } finally { fs.rmSync(dir, { recursive: true }); }
  });

  it('cursor_list shows file offsets after set', () => {
    const { dir, path: p } = newPlayerLog(['[12:00:00] x']);
    const store = new CursorStore(tmpCursorFile());
    try {
      runCursorSet({
        name: 'inspector',
        source: 'player',
        file: p,
        anchor: 'start',
      }, store);
      const list = runCursorList({}, store);
      const inspector = list.cursors.find((c) => c.name === 'inspector');
      assert.ok(inspector);
      assert.deepEqual(inspector!.sources.player!.files, { [p]: 0 });
    } finally { fs.rmSync(dir, { recursive: true }); }
  });
});

describe('context: N attaches surrounding lines', () => {
  it('captures up to N lines before and after each match', async () => {
    // Match is at line 4; want up to 2 before and 2 after.
    const { dir, path: p } = newPlayerLog([
      '[12:00:00] line one boring',
      '[12:00:01] line two boring',
      '[12:00:02] line three also boring',
      '[12:00:03] LocalPlayer: ProcessSetPetOwner(11, 22)',
      '[12:00:04] line five trailing',
      '[12:00:05] line six trailing',
      '[12:00:06] line seven trailing',
    ]);
    const cursorFile = tmpCursorFile();
    const store = new CursorStore(cursorFile);
    try {
      const args = QueryEventsInput.parse({
        source: 'player',
        event_type: ['samwise.SetPetOwner'],
        context: 2,
        limit: 10,
      });
      const result = await runQueryEvents(args, makeConfig(p), store);
      assert.equal(result.events.length, 1);
      const e = result.events[0]!;
      assert.ok(e.contextLines, 'contextLines should be present');
      assert.equal(e.contextLines!.before.length, 2);
      assert.equal(e.contextLines!.after.length, 2);
      assert.match(e.contextLines!.before[0]!, /line two boring/);
      assert.match(e.contextLines!.before[1]!, /line three also boring/);
      assert.match(e.contextLines!.after[0]!, /line five trailing/);
      assert.match(e.contextLines!.after[1]!, /line six trailing/);
    } finally {
      fs.rmSync(dir, { recursive: true });
      if (fs.existsSync(cursorFile)) fs.unlinkSync(cursorFile);
    }
  });

  it('handles end-of-file matches with partial after-context', async () => {
    const { dir, path: p } = newPlayerLog([
      '[12:00:00] noise one',
      '[12:00:01] noise two',
      '[12:00:02] LocalPlayer: ProcessSetPetOwner(99, 0)',
    ]);
    const cursorFile = tmpCursorFile();
    const store = new CursorStore(cursorFile);
    try {
      const args = QueryEventsInput.parse({
        source: 'player',
        event_type: ['samwise.SetPetOwner'],
        context: 5,
        limit: 10,
      });
      const result = await runQueryEvents(args, makeConfig(p), store);
      assert.equal(result.events.length, 1);
      const e = result.events[0]!;
      assert.equal(e.contextLines!.before.length, 2);
      assert.equal(e.contextLines!.after.length, 0,
        'end-of-file match has no following lines');
    } finally {
      fs.rmSync(dir, { recursive: true });
      if (fs.existsSync(cursorFile)) fs.unlinkSync(cursorFile);
    }
  });
});

describe('mithril serilog category pre-filter', () => {
  it('skips JSON.parse for lines whose Category is not in the allowlist', async () => {
    const dir = path.join(os.tmpdir(), `mithril-serilog-prefilter-${Date.now()}`);
    fs.mkdirSync(dir);
    try {
      const log = path.join(dir, 'mithril-test.json');
      fs.writeFileSync(log, [
        JSON.stringify({ '@t': new Date(Date.now() - 3_600_000).toISOString(), '@mt': '{Category} {Message}', Category: 'Reference', Message: 'load' }),
        // A malformed line with Category: 'Reference' should still parse fine.
        JSON.stringify({ '@t': new Date(Date.now() - 3_500_000).toISOString(), '@mt': '{Category} {Message}', Category: 'Reference', Message: 'cache hit' }),
        // A 'Samwise' line that should be skipped by pre-filter.
        JSON.stringify({ '@t': new Date(Date.now() - 3_400_000).toISOString(), '@mt': '{Category} {Message}', Category: 'Samwise', Message: 'plot ripened' }),
      ].join('\n') + '\n');

      const args = AggregateInput.parse({
        source: 'mithril',
        agg: 'count',
        event_type: ['mithril.Reference'],
        since: '2h',
      });
      const cursorFile = tmpCursorFile();
      const config = {
        playerLogPath: '',
        chatLogDir: '',
        mithrilLogDir: dir,
        characterRoot: '',
        shellSettingsPath: '',
      };
      const result = await runAggregate(args, config, new CursorStore(cursorFile));
      assert.equal(result.summary.matched, 2,
        'allowlist should keep only Reference events');
    } finally { fs.rmSync(dir, { recursive: true }); }
  });
});
