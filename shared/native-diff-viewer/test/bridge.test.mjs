import assert from 'node:assert/strict';
import { test } from 'node:test';
import { acceptHostMessage, makeViewerEvent } from '../src/bridge.mjs';

const snapshot = {
  kind: 'snapshot', generation: 3, identity: 'wt:a:staged:x',
  path: '<img src=x onerror=alert(1)>.cs',
  before: 'original\n', after: 'staged\n',
  beforeLabel: 'HEAD', afterLabel: 'index',
  mode: 'side-by-side',
  beforeFacts: { bom: false, eol: 'LF', finalNewline: true },
  afterFacts: { bom: false, eol: 'LF', finalNewline: true },
};

test('accepts immutable paired snapshot and refuses partial/unknown commands', () => {
  assert.deepEqual(acceptHostMessage(snapshot), snapshot);
  for (const change of [{ after: undefined }, { before: undefined }, { kind: 'writeFile' }, { arbitraryCommand: 'save' }, { generation: -1 }, { before: 22 }, { mode: 'edit' }, { path: 'a'.repeat(4097) }]) {
    assert.equal(acceptHostMessage({ ...snapshot, ...change }), null);
  }
});

test('rejects oversized messages and markup never becomes executable HTML', () => {
  assert.equal(acceptHostMessage({ ...snapshot, before: 'a'.repeat(1024 * 1024 + 1) }), null);
  assert.equal(acceptHostMessage({ ...snapshot, after: '<script>write()</script>' })?.after, '<script>write()</script>');
});

test('viewer emits only bounded ready or failure events (never file operations)', () => {
  assert.deepEqual(makeViewerEvent('ready'), { kind: 'ready' });
  assert.equal(makeViewerEvent('open-file', { path: 'x' }), null);
  assert.equal(makeViewerEvent('failure', { message: 'x'.repeat(2049) }), null);
});
