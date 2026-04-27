import { describe, it } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { loadCatalog } from '../src/parsing/catalog.js';
import { scanChatLogs } from '../src/sources/chat-log.js';

function setupChatDir(): string {
  const dir = path.join(os.tmpdir(), `mithril-chat-${Date.now()}-${Math.random()}`);
  fs.mkdirSync(dir);
  fs.writeFileSync(path.join(dir, 'Chat-26-04-25.log'),
    [
      '26-04-25 12:00:00\t[Status] The Iron Vein is 25m east and 30m north',
      '26-04-25 12:01:00\t[Status] Iron Ore x3 collected!',
      '26-04-25 12:02:00\t[Global] Emraell: hi everyone HOOOWL',
    ].join('\n') + '\n');
  fs.writeFileSync(path.join(dir, 'Chat-26-04-26.log'),
    [
      '26-04-26 09:00:00\t[Status] Apple x2 added to inventory.',
      '26-04-26 09:01:00\t[Status] Egg added to inventory.',
    ].join('\n') + '\n');
  return dir;
}

describe('scanChatLogs', () => {
  it('parses a survey line via the legolas catalog', async () => {
    const dir = setupChatDir();
    try {
      const catalog = loadCatalog();
      const stats = { scannedBytes: 0, scannedLines: 0, endOffsets: {}, rolledOverFiles: [] };
      const events: any[] = [];
      for await (const e of scanChatLogs(catalog, {
        dir,
        since: new Date('2026-04-25T00:00:00Z'),
        until: new Date('2026-04-25T23:59:59Z'),
      }, stats)) events.push(e);

      const survey = events.find((e) => e.type === 'legolas.SurveyDetected');
      assert.ok(survey);
      assert.equal(survey.data.name, 'Iron Vein');
      assert.equal(survey.data.aDir, 'east');
      assert.equal(survey.data.bDir, 'north');
    } finally { fs.rmSync(dir, { recursive: true }); }
  });

  it('parses inventory-status lines from the shared catalog', async () => {
    const dir = setupChatDir();
    try {
      const catalog = loadCatalog();
      const stats = { scannedBytes: 0, scannedLines: 0, endOffsets: {}, rolledOverFiles: [] };
      const events: any[] = [];
      for await (const e of scanChatLogs(catalog, {
        dir,
        since: new Date('2026-04-26T00:00:00Z'),
        until: new Date('2026-04-26T23:59:59Z'),
      }, stats)) events.push(e);

      const inv = events.filter((e) => e.type === 'shared.InventoryAdded');
      // Apple x2 hits CountedRx, Egg hits SingleRx.
      assert.ok(inv.length >= 2);
    } finally { fs.rmSync(dir, { recursive: true }); }
  });

  it('skips files outside the requested time window', async () => {
    const dir = setupChatDir();
    try {
      const catalog = loadCatalog();
      const stats = { scannedBytes: 0, scannedLines: 0, endOffsets: {}, rolledOverFiles: [] };
      const events: any[] = [];
      // Window is entirely before either file's date.
      for await (const e of scanChatLogs(catalog, {
        dir,
        since: new Date('2025-01-01T00:00:00Z'),
        until: new Date('2025-12-31T23:59:59Z'),
      }, stats)) events.push(e);
      assert.equal(events.length, 0);
      assert.equal(stats.scannedBytes, 0);
    } finally { fs.rmSync(dir, { recursive: true }); }
  });
});
