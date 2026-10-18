import * as monaco from 'monaco-editor/editor/editor.api.js';
import 'monaco-editor/editor/contrib/find/browser/findController.js';
import 'monaco-editor/basic-languages/monaco.contribution.js';
import 'monaco-editor/language/json/monaco.contribution.js';
import 'monaco-editor/language/css/monaco.contribution.js';
import 'monaco-editor/language/html/monaco.contribution.js';
import 'monaco-editor/language/typescript/monaco.contribution.js';
import { acceptHostMessage, makeViewerEvent } from './bridge.mjs';

globalThis.MonacoEnvironment = {
  getWorkerUrl(_moduleId, label) {
    const name = ({ json: 'json', css: 'css', scss: 'css', less: 'css', html: 'html', handlebars: 'html', razor: 'html', typescript: 'typescript', javascript: 'typescript' })[label] ?? 'editor';
    return new URL(`./workers/${name}.js`, import.meta.url).href;
  },
};
const root = document.getElementById('document');
let diff;
let original;
let modified;
let currentGeneration = -1;
const notify = event => {
  if (!event) return;
  if (window.chrome?.webview?.postMessage) window.chrome.webview.postMessage(event);
  else if (window.webkit?.messageHandlers?.gabCode?.postMessage) window.webkit.messageHandlers.gabCode.postMessage(event);
};
// Monaco treats both model values as inert text; never interpolate repo content into HTML.
const languageFor = path => {
  const extension = path.match(/\.([\w-]+)$/)?.[1]?.toLowerCase();
  return ({ cs: 'csharp', swift: 'swift', js: 'javascript', mjs: 'javascript', ts: 'typescript', json: 'json', html: 'html', css: 'css', py: 'python', md: 'markdown' })[extension] ?? 'plaintext';
};
function show(raw) {
  const message = acceptHostMessage(raw);
  if (!message || message.generation < currentGeneration) return;
  currentGeneration = message.generation;
  diff?.dispose(); original?.dispose(); modified?.dispose();
  const language = languageFor(message.path);
  original = monaco.editor.createModel(message.before, language);
  modified = monaco.editor.createModel(message.after, language);
  diff = monaco.editor.createDiffEditor(root, {
    readOnly: true, originalEditable: false, enableSplitViewResizing: true,
    renderSideBySide: message.mode !== 'inline',
    automaticLayout: true, accessibilitySupport: 'on',
    contextmenu: false, renderValidationDecorations: 'off',
    diffCodeLens: false, renderOverviewRuler: true,
  });
  diff.setModel({ original, modified });
  // No native file system services or save endpoint are wired into the document.
  root.setAttribute('aria-label', `Read-only ${message.beforeLabel} to ${message.afterLabel} diff for ${message.path}`);
}
const receive = event => { try { show(event.data); } catch (error) { notify(makeViewerEvent('failure', { message: String(error).slice(0, 2048) })); } };
window.chrome?.webview?.addEventListener('message', receive);
window.addEventListener('message', event => { if (event.source === window && event.origin === window.location.origin) receive(event); });
window.gabCodeViewer = { show, models: () => original && modified ? { before: original.getValue(), after: modified.getValue() } : null }; // Native host calls only show; models is a read-only test probe.
notify(makeViewerEvent('ready'));
