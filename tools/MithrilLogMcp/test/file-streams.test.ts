import { describe, it } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { lineStream } from '../src/util/file-streams.js';

async function collect(p: string) {
  const out: { line: string; lineNo: number; byteOffset: number; byteLength: number }[] = [];
  for await (const r of lineStream(p)) out.push(r);
  return out;
}

function tmpFile(content: string): string {
  const p = path.join(os.tmpdir(), `mithril-line-stream-${Date.now()}-${Math.random()}.log`);
  fs.writeFileSync(p, content);
  return p;
}

describe('lineStream', () => {
  it('reads three LF-terminated lines with correct offsets', async () => {
    const p = tmpFile('alpha\nbeta\ngamma\n');
    try {
      const out = await collect(p);
      assert.equal(out.length, 3);
      assert.deepEqual(out.map((r) => r.line), ['alpha', 'beta', 'gamma']);
      assert.deepEqual(out.map((r) => r.byteOffset), [0, 6, 11]);
      assert.deepEqual(out.map((r) => r.lineNo), [1, 2, 3]);
    } finally { fs.unlinkSync(p); }
  });

  it('strips trailing \\r on CRLF lines', async () => {
    const p = tmpFile('alpha\r\nbeta\r\n');
    try {
      const out = await collect(p);
      assert.deepEqual(out.map((r) => r.line), ['alpha', 'beta']);
    } finally { fs.unlinkSync(p); }
  });

  it('emits the trailing line even without a terminator', async () => {
    const p = tmpFile('alpha\nbeta');
    try {
      const out = await collect(p);
      assert.deepEqual(out.map((r) => r.line), ['alpha', 'beta']);
    } finally { fs.unlinkSync(p); }
  });

  it('counts UTF-8 bytes (not chars) for offsets', async () => {
    const p = tmpFile('café\nbeta\n'); // é = 2 bytes
    try {
      const out = await collect(p);
      assert.equal(out[0]!.byteOffset, 0);
      // "café\n" is c=1 a=1 f=1 é=2 \n=1 -> 6 bytes
      assert.equal(out[1]!.byteOffset, 6);
    } finally { fs.unlinkSync(p); }
  });
});
