import { AUTH_INPUT_LIMIT } from './http-paths.js';
import { isBffOpaque } from './session-store.js';

export { AUTH_INPUT_LIMIT } from './http-paths.js';
export const BFF_COOKIES = Object.freeze({
  session: '__Host-ls-session',
  browser: '__Host-ls-browser',
  csrf: '__Host-ls-csrf',
  login: '__Host-ls-login-',
  logout: '__Host-ls-logout-',
});
const prefix = '__Host-ls-';
const fixed = new Set<string>([BFF_COOKIES.session, BFF_COOKIES.browser, BFF_COOKIES.csrf]);
export class BffInputError extends Error {
  constructor() {
    super('BFF input refused');
    this.name = 'BffInputError';
  }
}

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
