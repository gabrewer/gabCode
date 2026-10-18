// Pure presentation projections from Monaco's one diff computation, not a second Git diff.
export function changeLocations(changes, lineCount) {
  if (!Array.isArray(changes)) return [];
  return changes.map(change => {
    // Monaco's getLineChanges uses end=0 for empty sides, including at the first line.
    const deleted = change.modifiedEndLineNumber === 0;
    const added = change.originalEndLineNumber === 0;
    const line = Math.max(1, Math.min(Math.max(1, lineCount), change.modifiedStartLineNumber));
    const kind = deleted ? 'deleted' : added ? 'added' : 'modified';
    return { line, kind, description: `${kind[0].toUpperCase()}${kind.slice(1)} change near resulting line ${line}`, charChanges: change.charChanges ?? [] };
  });
}

export function invisibleDifference(before, after, rawBefore = '', rawAfter = '', visibleBefore = rawBefore, visibleAfter = rawAfter) {
  const differences = [];
  if (before.bom !== after.bom) differences.push('UTF-8 BOM differs');
  if (before.eol !== after.eol) differences.push(`line endings ${before.eol} → ${after.eol}`);
  if (before.finalNewline !== after.finalNewline) differences.push('final newline differs');
  if (rawBefore !== rawAfter && visibleBefore === visibleAfter && differences.length === 0)
    differences.push('Source text differs in bytes not represented by Monaco');
  return differences.join('; ');
}
