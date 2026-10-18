import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
const dist = resolve(dirname(fileURLToPath(import.meta.url)), '..', 'dist');

test('offline bundle contains pinned licensed Monaco, CSP, worker inventory and verified hashes', async () => {
  const manifest = JSON.parse(await readFile(join(dist, 'manifest.json'), 'utf8'));
  assert.equal(manifest.monaco, '0.57.0');
  const inventory = new Set(manifest.assets.map(asset => asset.path));
  for (const path of ['index.html', 'viewer.js', 'viewer.css', 'MONACO-LICENSE.txt',
    'MONACO-THIRD-PARTY-NOTICES.txt', 'DOMPURIFY-LICENSE.txt', 'DOMPURIFY-LICENSE-MPL.txt',
    'MARKED-LICENSE.md', 'workers/editor.js',
    'workers/json.js', 'workers/css.js', 'workers/html.js', 'workers/typescript.js']) assert.ok(inventory.has(path), path);
  for (const asset of manifest.assets) {
    assert.match(asset.path, /^[A-Za-z0-9._/-]+$/);
    const bytes = await readFile(join(dist, asset.path));
    assert.equal(bytes.length, asset.bytes, asset.path);
    assert.equal(createHash('sha256').update(bytes).digest('hex'), asset.sha256, asset.path);
  }
  const html = await readFile(join(dist, 'index.html'), 'utf8');
  assert.match(html, /connect-src 'none'/);
  assert.match(html, /worker-src 'self' blob:/);
  assert.match(html, /style-src 'self' 'unsafe-inline'/);
  assert.match(html, /script-src 'self'/);
  assert.doesNotMatch(html, /https?:\/\//);
});
