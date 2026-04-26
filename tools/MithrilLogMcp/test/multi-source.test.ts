import { describe, it } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { loadCatalog } from '../src/parsing/catalog.js';
import { scanMultiSource } from '../src/sources/multi-source.js';

function setupAllSources() {
  const root = path.join(os.tmpdir(), `mithril-multi-${Date.now()}-${Math.random()}`);
  fs.mkdirSync(root);

  const playerLog = path.join(root, 'Player.log');
  fs.writeFileSync(playerLog,
    '[12:00:01] LocalPlayer: ProcessSetPetOwner(11111, 22222)\n' +
    '[12:00:03] ProcessDeltaFavor(0, "NPC_Marna", 50.0, True)\n');
  const future = new Date(Date.now() + 60_000);
  fs.utimesSync(playerLog, future, future);

  const chatDir = path.join(root, 'ChatLogs');
  fs.mkdirSync(chatDir);
  const todayStr = isoDate(future);
  fs.writeFileSync(path.join(chatDir, `Chat-${todayStr}.log`),
    `${todayStr} 12:00:02\t[Status] The Iron Vein is 5m east and 7m north\n`);

  const mithrilDir = path.join(root, 'mithril-logs');
  fs.mkdirSync(mithrilDir);
  fs.writeFileSync(path.join(mithrilDir, 'mithril-test.json'),
    JSON.stringify({ '@t': isoTodayAt(future, 12, 0, 0), '@mt': '{Category} {Message}', Category: 'Reference', Message: 'Loaded' }) + '\n');

  return {
    root,
    config: {
      playerLogPath: playerLog,
      chatLogDir: chatDir,
      mithrilLogDir: mithrilDir,
      characterRoot: '',
      shellSettingsPath: '',
    },
  };
}

function isoDate(d: Date): string {
  const y = String(d.getUTCFullYear()).slice(-2);
  const m = String(d.getUTCMonth() + 1).padStart(2, '0');
  const day = String(d.getUTCDate()).padStart(2, '0');
  return `${y}-${m}-${day}`;
}

function isoTodayAt(d: Date, h: number, m: number, s: number): string {
  return new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate(), h, m, s)).toISOString();
}

describe('scanMultiSource', () => {
  it('source: all yields events from every source merged in time order', async () => {
    const { root, config } = setupAllSources();
    try {
      const catalog = loadCatalog();
      const stats = { scannedBytes: 0, scannedLines: 0 };
      const events: any[] = [];
      for await (const e of scanMultiSource(catalog, config, {
        source: 'all',
        since: new Date(0),
        until: new Date('2099-01-01'),
      }, stats)) {
        events.push({ ts: e.ts, source: e.source, type: e.type });
      }

      // We should see at least one event from each source, and the merged
      // stream should be sorted by timestamp.
      const sources = new Set(events.map((e) => e.source));
      assert.ok(sources.has('player'), `expected a player event, got ${[...sources].join(',')}`);
      assert.ok(sources.has('chat'), `expected a chat event, got ${[...sources].join(',')}`);
      assert.ok(sources.has('mithril'), `expected a mithril event, got ${[...sources].join(',')}`);

      const tsList = events.map((e) => Date.parse(e.ts));
      for (let i = 1; i < tsList.length; i++) {
        assert.ok(tsList[i]! >= tsList[i - 1]!,
          `events not sorted at index ${i}: ${tsList.slice(0, i + 1).join(',')}`);
      }
    } finally { fs.rmSync(root, { recursive: true }); }
  });

  it('source: chat yields only chat events', async () => {
    const { root, config } = setupAllSources();
    try {
      const catalog = loadCatalog();
      const stats = { scannedBytes: 0, scannedLines: 0 };
      const events: any[] = [];
      for await (const e of scanMultiSource(catalog, config, {
        source: 'chat',
        since: new Date(0),
        until: new Date('2099-01-01'),
      }, stats)) events.push(e);
      assert.ok(events.length > 0);
      assert.ok(events.every((e) => e.source === 'chat'));
    } finally { fs.rmSync(root, { recursive: true }); }
  });
});
