import assert from 'node:assert/strict';
import { test } from 'node:test';
import { changeLocations, invisibleDifference } from '../src/presentation.mjs';

test('line map includes added/modified spans and boundary deletions', () => {
  const changes = changeLocations([
    { originalStartLineNumber: 1, originalEndLineNumber: 1, modifiedStartLineNumber: 1, modifiedEndLineNumber: 1,
      charChanges: [{ originalStartLineNumber: 1, originalStartColumn: 1, originalEndLineNumber: 1, originalEndColumn: 2,
        modifiedStartLineNumber: 1, modifiedStartColumn: 1, modifiedEndLineNumber: 1, modifiedEndColumn: 3 }] },
    { originalStartLineNumber: 2, originalEndLineNumber: 2, modifiedStartLineNumber: 1, modifiedEndLineNumber: 0 },
    { originalStartLineNumber: 0, originalEndLineNumber: 0, modifiedStartLineNumber: 4, modifiedEndLineNumber: 4 },
  ], 3);
  assert.deepEqual(changes.map(x => [x.line, x.kind]), [[1, 'modified'], [1, 'deleted'], [3, 'added']]);
  assert.equal(changeLocations([{ originalStartLineNumber: 1, originalEndLineNumber: 1, modifiedStartLineNumber: 0, modifiedEndLineNumber: 0 }], 1)[0].line, 1);
  assert.ok(changes[0].description.includes('Modified'));
  assert.ok(changes[1].description.includes('Deleted'));
});

test('BOM/EOL/final-newline facts stay visible when Monaco-normalized text is equal', () => {
  const oldFacts = { bom: true, eol: 'CRLF', finalNewline: true };
  const newFacts = { bom: false, eol: 'LF', finalNewline: false };
  assert.match(invisibleDifference(oldFacts, newFacts), /BOM/);
  assert.match(invisibleDifference(oldFacts, newFacts), /line endings/);
  assert.match(invisibleDifference(oldFacts, newFacts), /final newline/);
  assert.equal(invisibleDifference(newFacts, newFacts), '');
  assert.match(invisibleDifference(
    { bom: false, eol: 'mixed', finalNewline: true },
    { bom: false, eol: 'mixed', finalNewline: true },
    'first\r\nsecond\nthird\n', 'first\nsecond\r\nthird\n',
    'first\nsecond\nthird\n', 'first\nsecond\nthird\n'), /not represented/);
});
