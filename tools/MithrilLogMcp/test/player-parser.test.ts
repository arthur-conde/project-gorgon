import { describe, it } from 'node:test';
import * as assert from 'node:assert/strict';
import { loadCatalog } from '../src/parsing/catalog.js';
import { PlayerLineParser } from '../src/parsing/player-parser.js';

const T = new Date(Date.UTC(2026, 3, 15, 12, 0, 0));

function parse(line: string) {
  const parser = new PlayerLineParser(loadCatalog());
  return parser.parse(line, T, 'Player.log', 1, 0);
}

describe('PlayerLineParser', () => {
  it('parses samwise.SetPetOwner', () => {
    const events = parse('LocalPlayer: ProcessSetPetOwner(12345, 999)');
    const e = events.find((x) => x.type === 'samwise.SetPetOwner');
    assert.ok(e, 'expected samwise.SetPetOwner');
    assert.equal(e!.data.entityId, '12345');
  });

  it('parses samwise.AppearanceLoop with scale', () => {
    const events = parse('Download appearance loop @Carrot(scale=1.0)');
    const e = events.find((x) => x.type === 'samwise.AppearanceLoop');
    assert.ok(e);
    assert.equal(e!.data.modelName, 'Carrot');
    assert.equal(e!.data.scale, 1.0);
  });

  it('parses arwen.FavorUpdate from ProcessStartInteraction with NPC_ key', () => {
    const events = parse('ProcessStartInteraction(123, 456, 1500.5, True, "NPC_Marna")');
    const e = events.find((x) => x.type === 'arwen.FavorUpdate');
    assert.ok(e);
    assert.equal(e!.data.npcKey, 'NPC_Marna');
    assert.equal(e!.data.absoluteFavor, 1500.5);
    assert.equal(e!.data.entityId, 123);
  });

  it('parses smaug.NpcInteractionStarted from the same line', () => {
    // The same ProcessStartInteraction line matches both arwen and smaug catalogs;
    // the MCP server emits both events because consumers may want either view.
    const events = parse('ProcessStartInteraction(123, 456, 1500.5, True, "NPC_Marna")');
    const arwen = events.find((x) => x.type === 'arwen.FavorUpdate');
    const smaug = events.find((x) => x.type === 'smaug.NpcInteractionStarted');
    assert.ok(arwen, 'expected arwen.FavorUpdate');
    assert.ok(smaug, 'expected smaug.NpcInteractionStarted');
    assert.equal(smaug!.data.npcKey, 'NPC_Marna');
  });

  it('parses arwen.FavorDelta from ProcessDeltaFavor', () => {
    const events = parse('ProcessDeltaFavor(0, "NPC_Marna", 50.0, True)');
    const e = events.find((x) => x.type === 'arwen.FavorDelta');
    assert.ok(e);
    assert.equal(e!.data.npcKey, 'NPC_Marna');
    assert.equal(e!.data.delta, 50.0);
  });

  it('parses smaug.VendorItemSold', () => {
    const events = parse('ProcessVendorAddItem(420, RustyDagger(987654), True)');
    const e = events.find((x) => x.type === 'smaug.VendorItemSold');
    assert.ok(e);
    assert.equal(e!.data.price, 420);
    assert.equal(e!.data.internalName, 'RustyDagger');
    assert.equal(e!.data.instanceId, 987654);
  });

  it('parses samwise.PlantingCapReached', () => {
    const line =
      'ProcessErrorMessage(ItemUnusable, "Barley Seeds can\'t be used: You already have the maximum of that type of plant growing")';
    const events = parse(line);
    const e = events.find((x) => x.type === 'samwise.PlantingCapReached');
    assert.ok(e);
    assert.equal(e!.data.seedDisplayName, 'Barley Seeds');
  });

  it('parses pippin.FoodsConsumedReport from a ProcessBook line', () => {
    // The ProcessBook line stores newlines as literal `\n` two-char sequences.
    const line =
      'LocalPlayer: ProcessBook("Skill Info", "Foods Consumed:\\n\\n  Egg (HAS DAIRY): 4\\n  Apple: 2\\n", "SkillReport")';
    const events = parse(line);
    const e = events.find((x) => x.type === 'pippin.FoodsConsumedReport');
    assert.ok(e, 'expected pippin.FoodsConsumedReport');
    const foods = e!.data.foods as Array<{ name: string; count: number; tags: string[] }>;
    assert.equal(foods.length, 2);
    assert.deepEqual(foods[0], { name: 'Egg', count: 4, tags: ['DAIRY'] });
    assert.deepEqual(foods[1], { name: 'Apple', count: 2, tags: [] });
  });

  it('returns no events for an unrelated line', () => {
    const events = parse('Some unrelated debug message about Unity things');
    assert.equal(events.length, 0);
  });
});
