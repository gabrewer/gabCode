import * as monaco from 'monaco-editor/editor/editor.api.js';
import 'monaco-editor/editor/contrib/find/browser/findController.js';
import 'monaco-editor/basic-languages/monaco.contribution.js';
import 'monaco-editor/language/json/monaco.contribution.js';
import 'monaco-editor/language/css/monaco.contribution.js';
import 'monaco-editor/language/html/monaco.contribution.js';
import 'monaco-editor/language/typescript/monaco.contribution.js';
import { acceptHostMessage, makeViewerEvent } from './bridge.mjs';
import { changeLocations, invisibleDifference } from './presentation.mjs';

globalThis.MonacoEnvironment = {
  getWorkerUrl(_moduleId, label) {
    const name = ({ json: 'json', css: 'css', scss: 'css', less: 'css', html: 'html', handlebars: 'html', razor: 'html', typescript: 'typescript', javascript: 'typescript' })[label] ?? 'editor';
    return new URL(`./workers/${name}.js`, import.meta.url).href;
  },
};
const contrast = window.matchMedia('(forced-colors: active)');
const applyTheme = () => monaco.editor.setTheme(contrast.matches ? 'hc-black' : 'vs-dark');
contrast.addEventListener('change', applyTheme);
applyTheme();
const root = document.getElementById('document');
const controls = document.createElement('div');
controls.className = 'viewer-controls';
const status = document.createElement('span');
status.setAttribute('role', 'status');
status.setAttribute('aria-live', 'polite');
const previous = document.createElement('button');
previous.textContent = 'Previous change';
const next = document.createElement('button');
next.textContent = 'Next change';
const deletion = document.createElement('button');
deletion.textContent = 'View deletion in Inline';
deletion.hidden = true;
controls.append(previous, next, deletion, status);
const canvas = document.createElement('div');
canvas.className = 'viewer-canvas';
const diffRoot = document.createElement('div');
const fileRoot = document.createElement('div');
diffRoot.className = fileRoot.className = 'viewer-surface';
canvas.append(diffRoot, fileRoot);
root.append(controls, canvas);

let diff;
let fileEditor;
let original;
let modified;
let diffListener;
let decorations = [];
let locations = [];
let changeIndex = -1;
let currentGeneration = -1;
let currentIdentity = '';
let currentMode = 'side-by-side';
let currentFacts = '';
let lastLine = 1;
let computing = false;
let unavailable = '';
let computationTimer;
const notify = event => {
  if (!event) return;
  if (window.chrome?.webview?.postMessage) window.chrome.webview.postMessage(event);
  else if (window.webkit?.messageHandlers?.gabCode?.postMessage) window.webkit.messageHandlers.gabCode.postMessage(event);
};
// Content/path/labels are untrusted text; never inject them as HTML, executable URI or CSS.
const languageFor = path => {
  const extension = path.match(/\.([\w-]+)$/)?.[1]?.toLowerCase();
  return ({ cs: 'csharp', swift: 'swift', js: 'javascript', mjs: 'javascript', ts: 'typescript', json: 'json', html: 'html', css: 'css', py: 'python', md: 'markdown' })[extension] ?? 'plaintext';
};
const immutableOptions = {
  readOnly: true, domReadOnly: true, contextmenu: false, glyphMargin: true,
  formatOnPaste: false, formatOnType: false, dragAndDrop: false,
  dropIntoEditor: { enabled: false }, pasteAs: { enabled: false },
  renderValidationDecorations: 'off', accessibilitySupport: 'on',
  automaticLayout: true, minimap: { enabled: false },
  readOnlyMessage: { value: 'Read-only review document' },
};
function updateStatus() {
  const position = unavailable || (computing ? 'Computing changes…' : changeIndex < 0 ? currentFacts ? 'No Monaco text spans' : 'No text changes' : `Change ${changeIndex + 1} of ${locations.length}: ${locations[changeIndex].description}`);
  status.textContent = `${position}${currentFacts ? `; ${currentFacts}` : ''}`;
  deletion.hidden = currentMode === 'inline' || locations[changeIndex]?.kind !== 'deleted';
  previous.disabled = next.disabled = locations.length === 0;
}
function markChanges() {
  if (!fileEditor || !modified) return;
  decorations = fileEditor.deltaDecorations(decorations, locations.map(location => ({
    range: new monaco.Range(location.line, 1, location.line, 1),
    options: {
      isWholeLine: true, glyphMarginClassName: `review-marker review-${location.kind}`,
      glyphMarginHoverMessage: { value: location.description },
      overviewRuler: { color: location.kind === 'deleted' ? '#cb7d12' : '#3987da', position: monaco.editor.OverviewRulerLane.Full },
    },
  })));
}
function gotoChange(direction) {
  if (!locations.length) return;
  const index = changeIndex < 0 ? 0 : (changeIndex + direction + locations.length) % locations.length;
  changeIndex = index;
  lastLine = locations[index].line;
  const editor = currentMode === 'file' ? fileEditor : diff?.getModifiedEditor();
  editor?.setPosition({ lineNumber: lastLine, column: 1 });
  editor?.revealLineInCenter(lastLine);
  updateStatus();
}
function setMode(mode, preferredLine = null) {
  if (!['side-by-side', 'inline', 'file'].includes(mode) || !diff) return;
  const oldEditor = currentMode === 'file' ? fileEditor : diff.getModifiedEditor();
  const hadTextFocus = oldEditor?.hasTextFocus();
  const position = oldEditor?.getPosition();
  if (preferredLine !== null) lastLine = preferredLine;
  else if (position) lastLine = position.lineNumber;
  currentMode = mode;
  const isFile = mode === 'file';
  // Keep the underlying diff laid out so Monaco can compute the one change map in File mode.
  diffRoot.style.opacity = isFile ? '0' : '1';
  diffRoot.style.pointerEvents = isFile ? 'none' : 'auto';
  diffRoot.setAttribute('aria-hidden', isFile ? 'true' : 'false');
  diffRoot.inert = isFile;
  fileRoot.style.display = isFile ? 'block' : 'none';
  fileRoot.inert = !isFile;
  fileRoot.setAttribute('aria-hidden', isFile ? 'false' : 'true');
  diff.updateOptions({ renderSideBySide: mode === 'side-by-side' });
  if (isFile) fileEditor?.layout();
  else diff.layout();
  const active = isFile ? fileEditor : diff.getModifiedEditor();
  const line = Math.max(1, Math.min(modified?.getLineCount() ?? 1, lastLine));
  active?.setPosition({ lineNumber: line, column: 1 });
  active?.revealLineInCenter(line);
  if (hadTextFocus) active?.focus();
  updateStatus();
}
function show(message) {
  if (message.generation < currentGeneration) return;
  const previousEditor = currentMode === 'file' ? fileEditor : diff?.getModifiedEditor();
  const retainLine = previousEditor?.getPosition()?.lineNumber ?? lastLine;
  const retainFocus = previousEditor?.hasTextFocus() ?? false;
  currentGeneration = message.generation;
  currentIdentity = message.identity;
  clearTimeout(computationTimer);
  diffListener?.dispose();
  diff?.dispose(); fileEditor?.dispose(); original?.dispose(); modified?.dispose();
  decorations = []; locations = []; changeIndex = -1; computing = true; unavailable = '';
  const language = languageFor(message.path);
  original = monaco.editor.createModel(message.before, language);
  modified = monaco.editor.createModel(message.after, language);
  // readOnly blocks ordinary UI commands; these guards restore the authoritative
  // immutable pair if an editor command ever mutates a model by another path.
  const beforeModel = original;
  const afterModel = modified;
  const canonicalBefore = beforeModel.getValue();
  const canonicalAfter = afterModel.getValue();
  beforeModel.onDidChangeContent(() => { if (beforeModel.getValue() !== canonicalBefore) beforeModel.setValue(canonicalBefore); });
  afterModel.onDidChangeContent(() => { if (afterModel.getValue() !== canonicalAfter) afterModel.setValue(canonicalAfter); });
  diff = monaco.editor.createDiffEditor(diffRoot, {
    ...immutableOptions, originalEditable: false, renderSideBySide: true,
    renderMarginRevertIcon: false,
    diffAlgorithm: 'advanced', ignoreTrimWhitespace: false, maxComputationTime: 5000,
    diffCodeLens: false, hideUnchangedRegions: { enabled: true, contextLineCount: 3, minimumLineCount: 5, revealLineCount: 10 },
    renderOverviewRuler: true,
  });
  diff.setModel({ original, modified });
  fileEditor = monaco.editor.create(fileRoot, { ...immutableOptions, model: modified, lineNumbers: 'on' });
  diffListener = diff.onDidUpdateDiff(() => {
    if (message.generation !== currentGeneration || message.identity !== currentIdentity || unavailable) return;
    const result = diff.getDiffComputationResult();
    if (!result || result.quitEarly) {
      computing = false; unavailable = 'Diff unavailable: change computation exceeded its safety limit';
      notify(makeViewerEvent('failure', { message: unavailable, generation: currentGeneration, identity: currentIdentity }));
      updateStatus(); return;
    }
    clearTimeout(computationTimer);
    computing = false;
    locations = changeLocations(result.changes, modified.getLineCount());
    changeIndex = locations.length ? locations.reduce((best, location, index) =>
      Math.abs(location.line - retainLine) < Math.abs(locations[best].line - retainLine) ? index : best, 0) : -1;
    markChanges(); updateStatus();
  });
  currentFacts = invisibleDifference(message.beforeFacts, message.afterFacts,
    message.before, message.after, canonicalBefore, canonicalAfter);
  computationTimer = setTimeout(() => {
    if (message.generation !== currentGeneration || message.identity !== currentIdentity || !computing) return;
    computing = false; unavailable = 'Diff unavailable: change computation timed out';
    notify(makeViewerEvent('failure', { message: unavailable, generation: currentGeneration, identity: currentIdentity })); updateStatus();
  }, 7000);
  lastLine = Math.max(1, Math.min(modified.getLineCount(), retainLine));
  root.setAttribute('aria-label', `Read-only ${message.beforeLabel} to ${message.afterLabel} diff for ${message.path}`);
  setMode(message.mode, retainLine);
  if (retainFocus) (currentMode === 'file' ? fileEditor : diff?.getModifiedEditor())?.focus();
}
function receive(raw) {
  const message = acceptHostMessage(raw);
  if (!message) return;
  try {
    if (message.kind === 'snapshot') show(message);
    else if (message.generation === currentGeneration && message.identity === currentIdentity) {
      if (message.kind === 'mode') setMode(message.mode);
      if (message.kind === 'change') gotoChange(message.direction === 'next' ? 1 : -1);
      if (message.kind === 'find') (currentMode === 'file' ? fileEditor : diff?.getModifiedEditor())?.getAction('actions.find')?.run();
      if (message.kind === 'focus') (currentMode === 'file' ? fileEditor : diff?.getModifiedEditor())?.focus();
    }
  } catch (error) { notify(makeViewerEvent('failure', { message: String(error).slice(0, 2048), generation: message.generation, identity: message.identity })); }
}
previous.addEventListener('click', () => gotoChange(-1));
next.addEventListener('click', () => gotoChange(1));
deletion.addEventListener('click', () => { setMode('inline'); gotoChange(0); });
window.chrome?.webview?.addEventListener('message', event => receive(event.data));
window.addEventListener('message', event => {
  if (event.source === window && event.origin === window.location.origin) receive(event.data);
});
window.gabCodeViewer = {
  show: receive,
  models: () => original && modified ? { before: original.getValue(), after: modified.getValue(), mode: currentMode } : null,
  line: line => {
    const editor = currentMode === 'file' ? fileEditor : diff?.getModifiedEditor();
    if (Number.isSafeInteger(line) && line >= 1) editor?.setPosition({ lineNumber: line, column: 1 });
    return editor?.getPosition()?.lineNumber ?? 1;
  },
};
notify(makeViewerEvent('ready'));
