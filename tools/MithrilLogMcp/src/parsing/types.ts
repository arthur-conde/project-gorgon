/**
 * Wire shape of every parsed event the server emits.
 *
 * Events are deliberately small and schema-stable: a small fixed envelope (`type`,
 * `ts`, `module`, `source`, `file`, `line`, `byteOffset`) plus an open `data`
 * record holding regex-captured fields. Adding a field to a parser only adds keys
 * to `data`; consumers (Claude, jq) that ignore unknown keys keep working.
 */
export interface ParsedEvent {
  type: string;
  ts: string;
  module: string;
  source: string;
  file: string;
  line: number;
  byteOffset: number;
  data: Record<string, unknown>;
  /**
   * Player character active when this event was emitted, stamped by
   * {@link ActiveCharacterTracker}. Only populated for `source: "player"`
   * events (chat events expose `data.speaker` instead; mithril events are
   * app-internal and aren't bound to a character). Undefined for player
   * events that occur before the first observed `ProcessAddPlayer` line.
   */
  activeCharacter?: string;
  /**
   * Raw lines surrounding the matched line in the source file when the
   * caller requested `context: N`. Currently populated only for
   * `source: "player"`; other sources will land in a follow-up. The arrays
   * may be shorter than N if the match is near the start/end of the file
   * or the scan window.
   */
  contextLines?: { before: string[]; after: string[] };
}

export interface QuerySummary {
  matched: number;
  returned: number;
  truncated: boolean;
  scannedBytes: number;
  scannedLines: number;
  elapsedMs: number;
}

export interface QueryResult {
  summary: QuerySummary;
  events: ParsedEvent[];
}

export interface AggregateResult {
  summary: QuerySummary;
  agg: string;
  buckets: Array<{ key: string; count: number }>;
}
