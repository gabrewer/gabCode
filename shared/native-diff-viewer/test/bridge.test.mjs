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

test('mode and change-position controls must match selected immutable generation', () => {
  assert.deepEqual(acceptHostMessage({ kind: 'mode', generation: 3, identity: snapshot.identity, mode: 'inline' }),
    { kind: 'mode', generation: 3, identity: snapshot.identity, mode: 'inline' });
  assert.deepEqual(acceptHostMessage({ kind: 'change', generation: 3, identity: snapshot.identity, direction: 'next' }),
    { kind: 'change', generation: 3, identity: snapshot.identity, direction: 'next' });
  assert.equal(acceptHostMessage({ kind: 'change', generation: 3, identity: snapshot.identity, direction: 'save' }), null);
  assert.equal(acceptHostMessage({ kind: 'mode', generation: 3, identity: snapshot.identity, mode: 'edit' }), null);
  for (const kind of ['find', 'focus']) {
    assert.deepEqual(acceptHostMessage({ kind, generation: 3, identity: snapshot.identity }),
      { kind, generation: 3, identity: snapshot.identity });
    assert.equal(acceptHostMessage({ kind, generation: 3, identity: snapshot.identity, command: 'write' }), null);
  }
});

test('viewer emits only bounded ready or failure events (never file operations)', () => {
  assert.deepEqual(makeViewerEvent('ready'), { kind: 'ready' });
  assert.equal(makeViewerEvent('open-file', { path: 'x' }), null);
  assert.equal(makeViewerEvent('failure', { message: 'x'.repeat(2049), generation: 3, identity: 'selected' }), null);
  assert.equal(makeViewerEvent('failure', { message: 'no scope' }), null);
  assert.deepEqual(makeViewerEvent('failure', { message: 'unavailable', generation: 3, identity: 'selected' }),
    { kind: 'failure', message: 'unavailable', generation: 3, identity: 'selected' });
});
