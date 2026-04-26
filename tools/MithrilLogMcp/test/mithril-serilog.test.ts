import { describe, it } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { scanMithrilSerilog } from '../src/sources/mithril-serilog.js';

function setupSerilogDir(): string {
  const dir = path.join(os.tmpdir(), `mithril-serilog-${Date.now()}-${Math.random()}`);
  fs.mkdirSync(dir);
  fs.writeFileSync(path.join(dir, 'mithril-20260426.json'),
    [
      JSON.stringify({ '@t': '2026-04-26T12:00:00.0000000Z', '@mt': '{Category} {Message}', Category: 'Reference', Message: 'Loaded items from cache' }),
      JSON.stringify({ '@t': '2026-04-26T12:00:01.0000000Z', '@mt': '{Category} {Message}', Category: 'Samwise', Message: 'Plot 12 ripened' }),
      JSON.stringify({ '@t': '2026-04-26T12:00:02.0000000Z', '@mt': '{Category} {Message}', Category: 'PlayerLog', Message: 'Tail offset 5421', '@l': 'Verbose' }),
    ].join('\n') + '\n');
  // A non-Serilog file should be skipped without error.
  fs.writeFileSync(path.join(dir, 'something-else.json'), 'not even json\n');
  return dir;
}

describe('scanMithrilSerilog', () => {
  it('parses compact JSON lines into mithril.<Category> events', async () => {
    const dir = setupSerilogDir();
    try {
      const stats = { scannedBytes: 0, scannedLines: 0 };
      const events: any[] = [];
      for await (const e of scanMithrilSerilog({
        dir,
        since: new Date('2026-04-26T00:00:00Z'),
        until: new Date('2026-04-26T23:59:59Z'),
      }, stats)) events.push(e);

      assert.equal(events.length, 3);
      assert.equal(events[0].type, 'mithril.Reference');
      assert.equal(events[1].type, 'mithril.Samwise');
      assert.equal(events[2].type, 'mithril.PlayerLog');
      assert.equal(events[2].data.level, 'Verbose');
      assert.equal(events[0].data.message, 'Loaded items from cache');
    } finally { fs.rmSync(dir, { recursive: true }); }
  });

  it('skips non-Serilog .json files quietly', async () => {
    const dir = setupSerilogDir();
    try {
      const stats = { scannedBytes: 0, scannedLines: 0 };
      const events: any[] = [];
      for await (const e of scanMithrilSerilog({
        dir,
        since: new Date(0),
        until: new Date('2099-01-01'),
      }, stats)) events.push(e);
      assert.equal(events.length, 3);
    } finally { fs.rmSync(dir, { recursive: true }); }
  });
});
