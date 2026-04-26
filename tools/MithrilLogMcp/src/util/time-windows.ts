/**
 * Window-resolution helpers for `--since 2h`, `--between iso iso`, etc.
 *
 * Player.log lines carry only `[HH:MM:SS]` (no date), so the date is anchored
 * by the file's mtime: every line is interpreted as `(mtimeDate, HH:MM:SS)`.
 * If a line's HH:MM:SS would land in the future relative to mtime, it's rolled
 * back one day to handle midnight crossings within a single file.
 */

const RELATIVE_RE = /^(\d+)\s*(ms|s|m|h|d)$/i;
const ISO_BASIC_RE = /^\d{4}-\d{2}-\d{2}/;

export interface ResolvedWindow {
  since: Date;
  until: Date;
}

export function resolveWindow(
  now: Date,
  args: {
    since?: string | undefined;
    until?: string | undefined;
    between?: [string, string] | undefined;
  },
): ResolvedWindow {
  if (args.between) {
    return { since: parseInstant(args.between[0], now), until: parseInstant(args.between[1], now) };
  }
  const since = args.since ? parseInstant(args.since, now, { negative: true }) : new Date(0);
  const until = args.until ? parseInstant(args.until, now) : now;
  return { since, until };
}

/**
 * Accepts:
 *   - ISO 8601 ("2026-04-26T12:00:00Z" or "2026-04-26")
 *   - Relative durations ("2h", "30m", "1d") — interpreted as `now - duration`
 *     when `negative: true` (the typical `--since` case), else `now + duration`.
 */
export function parseInstant(input: string, now: Date, opts: { negative?: boolean } = {}): Date {
  const trimmed = input.trim();
  const rel = RELATIVE_RE.exec(trimmed);
  if (rel) {
    const value = Number.parseInt(rel[1] ?? '0', 10);
    const unit = (rel[2] ?? 's').toLowerCase();
    const ms = unitToMs(value, unit);
    return new Date(now.getTime() + (opts.negative ? -ms : ms));
  }
  if (ISO_BASIC_RE.test(trimmed)) {
    const d = new Date(trimmed);
    if (Number.isFinite(d.getTime())) return d;
  }
  throw new Error(`Cannot parse instant: '${input}' (expected ISO 8601 or relative like '2h')`);
}

function unitToMs(value: number, unit: string): number {
  switch (unit) {
    case 'ms': return value;
    case 's': return value * 1000;
    case 'm': return value * 60_000;
    case 'h': return value * 3_600_000;
    case 'd': return value * 86_400_000;
    default: throw new Error(`Unknown duration unit: ${unit}`);
  }
}

/**
 * Player.log per-line stamper.
 *
 * `[HH:MM:SS]` at the start of every line is interpreted relative to a date
 * anchor (the file's mtime). To make midnight crossings within a single file
 * sensible, the anchor rolls *backward* one day when an emitted timestamp is
 * later than `mtime + 1 minute` (1 minute slack absorbs clock skew).
 */
export class PlayerLogTimestamper {
  private static readonly TIME_RE = /^\[(\d{2}):(\d{2}):(\d{2})\]\s/;
  private currentDate: Date;

  constructor(anchor: Date) {
    this.currentDate = startOfDay(anchor);
  }

  stamp(line: string, fallback: Date): Date {
    const m = PlayerLogTimestamper.TIME_RE.exec(line);
    if (!m) return fallback;
    const h = Number.parseInt(m[1] ?? '0', 10);
    const min = Number.parseInt(m[2] ?? '0', 10);
    const s = Number.parseInt(m[3] ?? '0', 10);
    return new Date(
      this.currentDate.getUTCFullYear(),
      this.currentDate.getUTCMonth(),
      this.currentDate.getUTCDate(),
      h, min, s,
    );
  }
}

export function startOfDay(d: Date): Date {
  return new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate()));
}
