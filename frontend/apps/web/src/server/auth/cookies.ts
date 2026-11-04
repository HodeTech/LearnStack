import { AUTH_INPUT_LIMIT } from './http-paths.js';
import { BffInputError } from './input-error.js';
import { isBffOpaque } from './session-store.js';

export { AUTH_INPUT_LIMIT } from './http-paths.js';
export const BFF_COOKIES = Object.freeze({
  session: '__Host-ls-session',
  entry: '__Host-ls-entry',
  browser: '__Host-ls-browser',
  csrf: '__Host-ls-csrf',
  login: '__Host-ls-login-',
  logout: '__Host-ls-logout-',
});
const prefix = '__Host-ls-';
const fixed = new Set<string>([
  BFF_COOKIES.session,
  BFF_COOKIES.entry,
  BFF_COOKIES.browser,
  BFF_COOKIES.csrf,
]);
export { BffInputError } from './input-error.js';

/** Parse only the closed auth family. No decoding, aliases or chunk reconstruction. */
export function bffCookies(header: string | null): ReadonlyMap<string, string> {
  const values = new Map<string, string>();
  let bytes = 0;
  for (const member of (header ?? '').split(';')) {
    const pair = member.trim();
    const equal = pair.indexOf('=');
    const name = equal < 0 ? pair : pair.slice(0, equal);
    if (!name.startsWith(prefix)) continue;
    bytes += Buffer.byteLength(pair) + 2;
    if (bytes > AUTH_INPUT_LIMIT || equal < 0 || values.has(name)) throw new BffInputError();
    const transaction = name.startsWith(BFF_COOKIES.login)
      ? name.slice(BFF_COOKIES.login.length)
      : name.startsWith(BFF_COOKIES.logout)
        ? name.slice(BFF_COOKIES.logout.length)
        : null;
    const value = pair.slice(equal + 1);
    if ((!fixed.has(name) && !isBffOpaque(transaction)) || !isBffOpaque(value))
      throw new BffInputError();
    values.set(name, value);
  }
  return values;
}

export function bffCookie(name: string, value: string, maxAge: number): string {
  if (
    !isBffOpaque(value) ||
    !Number.isSafeInteger(maxAge) ||
    maxAge <= 0 ||
    (!fixed.has(name) &&
      ![BFF_COOKIES.login, BFF_COOKIES.logout].some(
        (type) => name.startsWith(type) && isBffOpaque(name.slice(type.length)),
      ))
  )
    throw new BffInputError();
  return name + '=' + value + '; Path=/; Max-Age=' + maxAge + '; HttpOnly; Secure; SameSite=Lax';
}
export function clearBffCookie(name: string): string {
  // Validate the exact name through the same closed writer; deletion grants no new grammar.
  bffCookie(name, 'A'.repeat(43), 1);
  return name + '=; Path=/; Max-Age=0; HttpOnly; Secure; SameSite=Lax';
}

/** Recovery deletes only names in our host-cookie namespace; it never reflects values. */
export function clearMalformedBffCookies(header: string | null): readonly string[] {
  const names = new Map<string, { pair: string; count: number }>();
  let inputBytes = 0;
  for (const member of (header ?? '').split(';')) {
    const pair = member.trim();
    const name = pair.split('=', 1)[0]!;
    if (!name.startsWith(prefix) || !/^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$/.test(name)) continue;
    inputBytes += Buffer.byteLength(pair) + 2;
    names.set(name, { pair, count: (names.get(name)?.count ?? 0) + 1 });
  }
  const edits: string[] = [];
  let bytes = 0;
  for (const [name, { pair, count }] of names) {
    if (count === 1 && inputBytes <= AUTH_INPUT_LIMIT) {
      try {
        bffCookies(pair);
        continue;
      } catch (error) {
        if (!(error instanceof BffInputError)) throw error;
      }
    }
    const edit = name + '=; Path=/; Max-Age=0; HttpOnly; Secure; SameSite=Lax';
    if (bytes + Buffer.byteLength(edit) > AUTH_INPUT_LIMIT) break;
    bytes += Buffer.byteLength(edit);
    edits.push(edit);
  }
  return Object.freeze(edits);
}
