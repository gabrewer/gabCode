import { build } from 'esbuild';
import { appendFile, copyFile, lstat, mkdir, readdir, readFile, rm, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const out = join(root, 'dist');
// Only replace this package's generated output; reject a symlink to any other tree.
const existing = await lstat(out).catch(error => { if (error.code === 'ENOENT') return null; throw error; });
if (existing && !existing.isDirectory()) throw new Error('Generated output is not a directory');
if (existing) await rm(out, { recursive: true, force: false });
await mkdir(join(out, 'workers'), { recursive: true });
const workerEntries = {
  editor: 'monaco-editor/editor/editor.worker.js',
  json: 'monaco-editor/language/json/json.worker.js',
  css: 'monaco-editor/language/css/css.worker.js',
  html: 'monaco-editor/language/html/html.worker.js',
  typescript: 'monaco-editor/language/typescript/ts.worker.js',
};
for (const [name, entry] of Object.entries(workerEntries)) {
  await build({ entryPoints: [entry], bundle: true, minify: true, format: 'iife', platform: 'browser',
    target: 'es2022', outfile: join(out, 'workers', `${name}.js`), legalComments: 'none' });
}
await build({ entryPoints: [join(root, 'src/viewer.mjs')], bundle: true, minify: true,
  format: 'esm', platform: 'browser', target: 'es2022', outdir: out, entryNames: 'viewer',
  loader: { '.ttf': 'file', '.woff': 'file', '.woff2': 'file' }, assetNames: 'assets/[name]-[hash]',
  legalComments: 'none' });
await copyFile(join(root, 'src/index.html'), join(out, 'index.html'));
await appendFile(join(out, 'viewer.css'), await readFile(join(root, 'src/viewer.css')));
for (const [packageName, relativeName, destination] of [
  ['monaco-editor', 'LICENSE', 'MONACO-LICENSE.txt'],
  ['monaco-editor', 'ThirdPartyNotices.txt', 'MONACO-THIRD-PARTY-NOTICES.txt'],
  ['dompurify', 'LICENSE', 'DOMPURIFY-LICENSE.txt'],
  ['dompurify', 'LICENSE-MPL', 'DOMPURIFY-LICENSE-MPL.txt'],
  ['marked', 'LICENSE.md', 'MARKED-LICENSE.md'],
]) {
  await copyFile(join(root, 'node_modules', packageName, relativeName), join(out, destination));
}
async function files(directory) {
  const result = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    if (entry.name === 'manifest.json') continue;
    if (entry.isDirectory()) result.push(...await files(join(directory, entry.name)));
    else if (entry.isFile()) result.push(join(directory, entry.name));
  }
  return result;
}
const assets = [];
for (const file of (await files(out)).sort()) {
  const bytes = await readFile(file);
  assets.push({ path: relative(out, file).replaceAll('\\', '/'), bytes: bytes.length,
    sha256: createHash('sha256').update(bytes).digest('hex') });
}
await writeFile(join(out, 'manifest.json'), `${JSON.stringify({ monaco: '0.57.0', assets }, null, 2)}\n`);
console.log(`Bundled ${assets.length} files in ${out}`);
