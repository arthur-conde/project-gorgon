import type { ParsedEvent } from './types.js';

/**
 * Tracks the currently active player character by watching
 * `shared.ProcessAddPlayer` events as they flow through a Player.log scan.
 * Mirrors `Mithril.Shared.Character.ActiveCharacterLogSynchronizer` but lives
 * inside the per-scan pipeline so every emitted event can be stamped with
 * the active character at the time the line was written.
 *
 * "Active before stamp": when a ProcessAddPlayer event itself is emitted,
 * `observe()` runs first, so the event's `activeCharacter` reflects the
 * *new* character. This makes `character: "X"` queries correctly return
 * the login event that began X's session plus everything afterwards.
 */
export class ActiveCharacterTracker {
  private current: string | undefined;

  constructor(initial?: string) {
    this.current = initial;
  }

  /** Sets the initial value, e.g. from a backward-scan backfill on cursor resume. */
  set(name: string): void {
    this.current = name;
  }

  /** Updates the tracker if `ev` is a ProcessAddPlayer. No-op otherwise. */
  observe(ev: ParsedEvent): void {
    if (ev.type !== 'shared.ProcessAddPlayer') return;
    const name = ev.data.characterName;
    if (typeof name === 'string' && name.length > 0) this.current = name;
  }

  get active(): string | undefined {
    return this.current;
  }
}
