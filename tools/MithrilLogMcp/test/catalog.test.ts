import { describe, it } from 'node:test';
import * as assert from 'node:assert/strict';
import { loadCatalog } from '../src/parsing/catalog.js';

describe('LogPatternCatalog', () => {
  it('loads from the repo-root JSON without a configured path', () => {
    const cat = loadCatalog();
    assert.equal(cat.document.version, 1);
    assert.ok(cat.entries.length > 0);
  });

  it('groups entries by source', () => {
    const cat = loadCatalog();
    const player = cat.bySource.get('player') ?? [];
    const chat = cat.bySource.get('chat') ?? [];
    assert.ok(player.length >= 10, `expected >=10 player entries, got ${player.length}`);
    assert.ok(chat.length >= 5, `expected >=5 chat entries, got ${chat.length}`);
  });

  it('compiles every regex without error', () => {
    const cat = loadCatalog();
    for (const e of cat.entries) {
      assert.ok(e.regex instanceof RegExp, `entry ${e.key} is not a RegExp`);
    }
  });

  it('exposes the session-marker literal for tail readers', () => {
    const cat = loadCatalog();
    assert.equal(cat.sessionMarker.literal, 'ProcessAddPlayer(');
    assert.ok(cat.sessionMarker.scanChunkBytes > 0);
  });

  it('exposes byEventType for typed lookups', () => {
    const cat = loadCatalog();
    const samwise = cat.byEventType.get('samwise.SetPetOwner');
    assert.ok(samwise && samwise.length > 0);
  });
});
