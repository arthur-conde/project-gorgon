import * as fs from 'node:fs';

/**
 * Yields lines from a file along with their starting byte offset and 1-based
 * line number. Unlike `readline`, byte offsets are exact across UTF-8 — they
 * count file bytes, not decoded characters — so consumers can `Read` the
 * raw line later by seeking to (byteOffset, byteOffset + line.length).
 *
 * Operates on a byte range [start, end) so callers can resume from a cursor
 * or restrict to a sub-region without re-reading the whole file.
 */
export interface LineRecord {
  line: string;
  lineNo: number;
  byteOffset: number;
  byteLength: number;
}

export interface LineStreamOptions {
  start?: number;
  end?: number;
  /** First line number to emit. Defaults to 1. */
  startLineNo?: number;
}

const DEFAULT_HIGH_WATER_MARK = 1024 * 1024;

export async function* lineStream(
  path: string,
  opts: LineStreamOptions = {},
): AsyncGenerator<LineRecord, void, void> {
  const start = opts.start ?? 0;
  const end = opts.end;
  let lineNo = opts.startLineNo ?? 1;

  const stream = fs.createReadStream(path, {
    start,
    ...(end !== undefined ? { end: end - 1 } : {}),
    highWaterMark: DEFAULT_HIGH_WATER_MARK,
  });

  let buffer: Buffer = Buffer.alloc(0);
  let absoluteOffset = start;
  let pendingLineStart = start;

  for await (const chunk of stream as AsyncIterable<Buffer>) {
    buffer = buffer.length === 0
      ? Buffer.from(chunk)
      : Buffer.concat([buffer, chunk]);

    while (true) {
      const nl = buffer.indexOf(0x0a);
      if (nl < 0) break;

      // Slice off the line bytes (excluding the terminator), then trim a
      // trailing \r if the file uses CRLF.
      let lineEnd = nl;
      if (lineEnd > 0 && buffer[lineEnd - 1] === 0x0d) lineEnd = lineEnd - 1;

      const lineBytes = buffer.subarray(0, lineEnd);
      const lineText = lineBytes.toString('utf8');
      const fullLineByteLength = nl + 1; // include trailing \n
      yield {
        line: lineText,
        lineNo,
        byteOffset: pendingLineStart,
        byteLength: fullLineByteLength,
      };
      lineNo += 1;
      pendingLineStart += fullLineByteLength;
      buffer = buffer.subarray(nl + 1);
    }

    absoluteOffset = pendingLineStart + buffer.length;
  }

  // Final trailing line without a terminator.
  if (buffer.length > 0) {
    const lineEnd = buffer.length > 0 && buffer[buffer.length - 1] === 0x0d
      ? buffer.length - 1
      : buffer.length;
    yield {
      line: buffer.subarray(0, lineEnd).toString('utf8'),
      lineNo,
      byteOffset: pendingLineStart,
      byteLength: buffer.length,
    };
  }

  // Mark unused for now but keeps a parallel API surface if callers later want
  // to know how far the stream advanced.
  void absoluteOffset;
}

/**
 * Returns the number of bytes the stream would scan for the requested range.
 * Used to populate `summary.scannedBytes` without keeping a separate counter.
 */
export function scannedBytes(stat: fs.Stats, start = 0, end?: number): number {
  const upper = end ?? stat.size;
  return Math.max(0, upper - start);
}
