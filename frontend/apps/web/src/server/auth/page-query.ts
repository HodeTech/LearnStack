import { BffInputError } from './input-error.js';
import { isNativeReturnPath } from './session-path.js';

export const MAX_TARGET_BYTES = 8192;

function containsControl(value: string): boolean {
  return [...value].some(
    (character) => character.charCodeAt(0) < 32 || character.charCodeAt(0) === 127,
  );
}

/** Only ASCII targets are admitted, so their character and UTF-8 byte counts agree. */
export function validTarget(value: string): boolean {
  if (
    value.length > MAX_TARGET_BYTES ||
    !value.startsWith('/') ||
    value.startsWith('//') ||
    containsControl(value) ||
    /[\s\\"<>#]/.test(value) ||
    [...value].some((character) => character.charCodeAt(0) > 126)
  )
    return false;
  const path = value.split('?')[0] ?? '';
  // Next emits this redirect before middleware, independent of normalization flags.
  if (path.includes('//')) return false;
  // Refuse path identities that URL normalization would turn into another route.
  if (/%(?:2f|5c|00|0a|0d)/i.test(path)) return false;
  try {
    const decoded = decodeURIComponent(path);
    return (
      !decoded.split('/').some((segment) => segment === '.' || segment === '..') &&
      !containsControl(decoded) &&
      !decoded.includes('\\')
    );
  } catch {
    return false;
  }
}

export function bffQueryFields(text: string, allowed: readonly string[]) {
  const query = new URLSearchParams();
  try {
    for (const pair of text ? text.split('&') : []) {
      const equal = pair.indexOf('=');
      if (!pair || equal < 0) throw new BffInputError();
      const decode = (value: string) => decodeURIComponent(value.replace(/\+/g, ' '));
      const key = decode(pair.slice(0, equal));
      if (!allowed.includes(key) || query.has(key)) throw new BffInputError();
      query.set(key, decode(pair.slice(equal + 1)));
    }
  } catch {
    throw new BffInputError();
  }
  return query;
}
export function bffQuery(target: string, allowed: readonly string[]): URLSearchParams {
  if (!validTarget(target)) throw new BffInputError();
  const question = target.indexOf('?');
  return bffQueryFields(question < 0 ? '' : target.slice(question + 1), allowed);
}

export function bffCursor(value: string | null): string | undefined {
  if (value === null) return undefined;
  if (!value || value.length > 1024 || !/^[A-Za-z0-9_-]+$/.test(value)) throw new BffInputError();
  const remainder = value.length % 4;
  const last = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_'.indexOf(
    value[value.length - 1]!,
  );
  // Unpadded base64url cannot end in one symbol; unused trailing bits must be zero.
  if (
    remainder === 1 ||
    (remainder === 2 && last % 16 !== 0) ||
    (remainder === 3 && last % 4 !== 0)
  )
    throw new BffInputError();
  return value;
}
export function bffPageQuery(fields: URLSearchParams) {
  const cursor = bffCursor(fields.get('cursor'));
  const raw = fields.get('limit');
  if (raw !== null && (!/^[1-9][0-9]{0,9}$/.test(raw) || Number(raw) > 2147483647))
    throw new BffInputError();
  return Object.freeze({ cursor, limit: raw === null ? undefined : Math.min(Number(raw), 100) });
}

/** One grammar for public admission and native protected-page preparation. */
export function bffDocumentQuery(target: string, nativeFlight = false) {
  const path = target.split('?', 1)[0]!;
  if (path === '/' || !isNativeReturnPath(path)) throw new BffInputError();
  const allowed = path === '/studio/members' ? ['cursor', 'limit'] : [];
  const fields = bffQuery(target, nativeFlight ? [...allowed, '_rsc'] : allowed);
  if (fields.has('_rsc')) {
    if (!/^[\x21-\x7e]{1,128}$/.test(fields.get('_rsc')!)) throw new BffInputError();
    fields.delete('_rsc');
  }
  return Object.freeze({ target: path, ...bffPageQuery(fields) });
}
