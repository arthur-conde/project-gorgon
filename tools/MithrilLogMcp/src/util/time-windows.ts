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
    between?: string[] | undefined;
  },
): ResolvedWindow {
  if (args.between) {
    const [start, end] = args.between as [string, string];
    const since = parseInstant(start, now);
    const until = parseInstant(end, now);
    if (since > until) {
      throw new Error(
        `'between' range is reversed: '${start}' (${since.toISOString()}) ` +
        `is after '${end}' (${until.toISOString()}). Pass [start, end].`,
      );
    }
    return { since, until };
  }
  const since = args.since ? parseInstant(args.since, now, { negative: true }) : new Date(0);
  const until = args.until ? parseInstant(args.until, now) : now;
  if (since > until) {
    throw new Error(
      `'since' (${since.toISOString()}) is after 'until' (${until.toISOString()}). ` +
      `Check the duration sign or ISO timestamps.`,
    );
  }
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
 * Center-and-radius window form. `at` is the center instant (ISO 8601 or a
 * relative duration interpreted as `now - duration`); `around` is a duration
 * (e.g. "30s", "1m") that becomes the half-width. The resolved window is
 * `[at - around, at + around]`. Defaults to ±1m if `around` is omitted.
 */
export function resolveAtAround(
  now: Date,
  at: string,
  around: string | undefined,
): ResolvedWindow {
  const center = parseInstant(at, now, { negative: true });
  const radiusMs = parseDuration(around ?? '1m');
  return {
    since: new Date(center.getTime() - radiusMs),
    until: new Date(center.getTime() + radiusMs),
  };
}

function parseDuration(input: string): number {
  const m = RELATIVE_RE.exec(input.trim());
  if (!m) throw new Error(`Cannot parse duration: '${input}' (expected '30s', '1m', '5h', etc.)`);
  return unitToMs(Number.parseInt(m[1] ?? '0', 10), (m[2] ?? 's').toLowerCase());
}

/**
 * Player.log per-line stamper.
 *
 * Lines carry only `[HH:MM:SS]` (UTC) — no date. The date anchor for the
 * *first* line is supplied by the caller (typically computed by
 * {@link countPlayerLogCrossings}: the file's mtime UTC date, walked back
 * one day per midnight crossing detected in the file). As we stream forward,
 * a backward jump in HH:MM:SS of more than ~1 minute is taken as a midnight
 * crossing and the running date advances by one day.
 *
 * The game writes Player.log timestamps in UTC (cross-checked against
 * ChatLogs/ entries, which are local-TZ — see issue #24). Times are
 * constructed with `Date.UTC(...)` so the resulting instant matches the
 * `since`/`until` window without a TZ-offset shift.
 */
export class PlayerLogTimestamper {
  static readonly TIME_RE = /^\[(\d{2}):(\d{2}):(\d{2})\]\s/;
  private static readonly BACKWARD_JUMP_SLACK_SECONDS = 60;

  private currentDate: Date;
  private prevSeconds = -1;

  constructor(startDate: Date) {
    this.currentDate = new Date(Date.UTC(
      startDate.getUTCFullYear(), startDate.getUTCMonth(), startDate.getUTCDate(),
    ));
  }

  stamp(line: string, fallback: Date): Date {
    const m = PlayerLogTimestamper.TIME_RE.exec(line);
    if (!m) return fallback;
    const h = Number.parseInt(m[1] ?? '0', 10);
    const min = Number.parseInt(m[2] ?? '0', 10);
    const s = Number.parseInt(m[3] ?? '0', 10);

    const seconds = h * 3600 + min * 60 + s;
    if (this.prevSeconds >= 0 &&
        seconds < this.prevSeconds - PlayerLogTimestamper.BACKWARD_JUMP_SLACK_SECONDS) {
      // HH:MM:SS dropped — the file crossed midnight; advance the date.
      this.currentDate = new Date(this.currentDate);
      this.currentDate.setUTCDate(this.currentDate.getUTCDate() + 1);
    }
    this.prevSeconds = seconds;

    return new Date(Date.UTC(
      this.currentDate.getUTCFullYear(),
      this.currentDate.getUTCMonth(),
      this.currentDate.getUTCDate(),
      h, min, s,
    ));
  }
}

export function startOfDay(d: Date): Date {
  return new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate()));
}

/**
 * First pass over a Player.log: counts how many midnight crossings the file
 * contains by walking `[HH:MM:SS]` markers. Combined with the file's mtime,
 * lets us anchor the *start* of the file rather than the end.
 *
 * Without this, the timestamper would naively anchor every line to the mtime's
 * UTC date — which is wrong for any line earlier in the day than the current
 * mtime time, and badly wrong for lines from previous days that the game has
 * accumulated in the same Player.log.
 */
export async function countPlayerLogCrossings(
  lines: AsyncIterable<{ line: string }>,
  slackSeconds = 60,
): Promise<number> {
  let prev = -1;
  let crossings = 0;
  for await (const rec of lines) {
    const m = PlayerLogTimestamper.TIME_RE.exec(rec.line);
    if (!m) continue;
    const h = Number.parseInt(m[1] ?? '0', 10);
    const min = Number.parseInt(m[2] ?? '0', 10);
    const s = Number.parseInt(m[3] ?? '0', 10);
    const seconds = h * 3600 + min * 60 + s;
    if (prev >= 0 && seconds < prev - slackSeconds) crossings++;
    prev = seconds;
  }
  return crossings;
}
