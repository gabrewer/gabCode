// Deliberately narrow presentation-only seam. Hosts independently validate origin,
// length and generation; this guard is defense in depth, never a Git authority.
const own = (object, key) => Object.hasOwn(object, key);
const facts = value => value && typeof value === 'object' &&
  typeof value.bom === 'boolean' && typeof value.finalNewline === 'boolean' &&
  typeof value.eol === 'string' && ['LF', 'CRLF', 'CR', 'mixed', 'none'].includes(value.eol) &&
  Object.keys(value).every(key => ['bom', 'finalNewline', 'eol'].includes(key));

export function acceptHostMessage(value) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
  const keys = ['kind', 'generation', 'identity', 'path', 'before', 'after', 'beforeLabel', 'afterLabel', 'mode', 'beforeFacts', 'afterFacts'];
  if (['mode', 'change', 'find', 'focus'].includes(value.kind)) {
    const controlKeys = value.kind === 'mode' ? ['kind', 'generation', 'identity', 'mode'] :
      value.kind === 'change' ? ['kind', 'generation', 'identity', 'direction'] : ['kind', 'generation', 'identity'];
    if (Object.keys(value).length !== controlKeys.length || !Object.keys(value).every(key => controlKeys.includes(key)) ||
        !Number.isSafeInteger(value.generation) || value.generation < 0 ||
        typeof value.identity !== 'string' || value.identity.length === 0 || value.identity.length > 512) return null;
    if (value.kind === 'mode') return ['side-by-side', 'inline', 'file'].includes(value.mode) ? value : null;
    if (value.kind === 'change') return ['next', 'previous'].includes(value.direction) ? value : null;
    return value;
  }
  if (!Object.keys(value).every(key => keys.includes(key))) return null;
  if (value.kind !== 'snapshot' || !Number.isSafeInteger(value.generation) || value.generation < 0 ||
      !['side-by-side', 'inline', 'file'].includes(value.mode) ||
      !['identity', 'path', 'before', 'after', 'beforeLabel', 'afterLabel'].every(key => own(value, key) && typeof value[key] === 'string') ||
      value.identity.length > 512 || value.identity.length === 0 || value.path.length > 4096 ||
      value.beforeLabel.length > 256 || value.afterLabel.length > 256 ||
      value.before.length > 1024 * 1024 || value.after.length > 1024 * 1024 ||
      !facts(value.beforeFacts) || !facts(value.afterFacts)) return null;
  return value;
}

export function makeViewerEvent(kind, fields = {}) {
  if (kind === 'ready' && Object.keys(fields).length === 0) return { kind };
  if (kind === 'failure' && typeof fields.message === 'string' && fields.message.length <= 2048 &&
      Number.isSafeInteger(fields.generation) && fields.generation >= 0 &&
      typeof fields.identity === 'string' && fields.identity.length > 0 && fields.identity.length <= 512 &&
      Object.keys(fields).length === 3 && Object.keys(fields).every(key => ['message', 'generation', 'identity'].includes(key)))
    return { kind, message: fields.message, generation: fields.generation, identity: fields.identity };
  return null;
}
