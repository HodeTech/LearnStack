import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

import { browserFailureDetail } from './admission-browser.mjs';
import { createPrivateScanner } from './fixture-support.mjs';

/** Preserve only the readiness helper's branded, finite observations. */
export function bffDevToolsFailureStage(error) {
  return (
    new Map([
      [' browser-launch', 'browser-not-live'],
      [' browser-devtools owner-inactive', 'browser-devtools-inactive'],
      [' browser-devtools port-unreadable', 'browser-devtools-unreadable'],
      [' browser-devtools deadline-missing', 'browser-devtools-missing'],
      [' browser-devtools deadline-empty', 'browser-devtools-empty'],
      [' browser-devtools deadline-malformed', 'browser-devtools-malformed'],
    ]).get(browserFailureDetail(error)) ?? 'browser-devtools'
  );
}

const catalogues = {
  en: JSON.parse(
    readFileSync(new URL('../src/i18n/messages/en/auth.json', import.meta.url), 'utf8'),
  ),
  tr: JSON.parse(
    readFileSync(new URL('../src/i18n/messages/tr/auth.json', import.meta.url), 'utf8'),
  ),
};

/** Expectations come from the selected committed catalogue, independently of rendered HTML. */
export function bffProofCopy(locale, key) {
  assert.ok(['en', 'tr', 'tr-TR'].includes(locale), 'closed proof locale');
  assert.ok(
    [
      'signin.continue',
      'shell.studio',
      'shell.account',
      'shell.portal',
      'session.extended',
      'session.javascript_required',
      'entry.confirm',
    ].includes(key),
    'closed proof copy',
  );
  const [group, name] = key.split('.');
  const value = catalogues[locale === 'en' ? 'en' : 'tr'][group][name];
  assert.ok(typeof value === 'string' && value.length > 0, 'nonempty proof copy');
  return value;
}

/** Private fixture selector only; application admission remains independent. */
export function bffPageKind(path) {
  const fixed = {
    '/studio': 'studio',
    '/portal': 'portal',
    '/studio/members': 'members',
    '/studio/roles': 'roles',
    '/portal/profile': 'profile',
  };
  if (typeof path !== 'string') return null;
  if (Object.hasOwn(fixed, path)) return fixed[path];
  const detail =
    /^\/studio\/(members|roles)\/[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.exec(
      path,
    );
  return detail ? (detail[1] === 'members' ? 'member' : 'role') : null;
}

/** Browser cookie jars default absent SameSite to Lax; inspect the actual header too. */
export function assertBffCookieAttributes(headers) {
  for (const header of headers ?? []) {
    if (!header.startsWith('__Host-ls-')) continue;
    assert.ok(/;\s*Path=\/(?:;|$)/i.test(header), 'owned cookie root path');
    assert.ok(/;\s*HttpOnly(?:;|$)/i.test(header), 'owned cookie HttpOnly');
    assert.ok(/;\s*Secure(?:;|$)/i.test(header), 'owned cookie Secure');
    assert.ok(/;\s*SameSite=Lax(?:;|$)/i.test(header), 'owned cookie explicit SameSite');
    assert.ok(!/;\s*Domain=/i.test(header), 'owned cookie no Domain');
  }
}

export function assertBffPrivateOutput(scanners) {
  assert.equal(
    scanners.some((scanner) => scanner.leaked),
    false,
    'BFF proof private output',
  );
}

// Cookie values become known only after a response. Retain the complete bounded
// output until then; overflow refuses the proof instead of losing earlier bytes.
const maximumCookieOutputBytes = 256 * 1024;
export function createBffCookieScanner(canaries) {
  const scanner = createPrivateScanner(canaries);
  const chunks = [];
  let bytes = 0;
  let leaked = false;
  const observed = () => Buffer.concat(chunks, bytes).toString('utf8');
  const check = () => {
    leaked ||= scanner.contains(observed());
  };
  return {
    contains: scanner.contains,
    push(chunk) {
      const copy = Buffer.from(chunk);
      if (bytes + copy.byteLength > maximumCookieOutputBytes) {
        leaked = true;
        return;
      }
      chunks.push(copy);
      bytes += copy.byteLength;
    },
    finish: check,
    get leaked() {
      check();
      return leaked;
    },
    get tail() {
      return observed();
    },
  };
}

/** Attach every rejection handler before starting the action that may fail. */
export async function withProofWaiters(waiters, action) {
  const results = await Promise.all([...waiters, Promise.resolve().then(action)]);
  return results.slice(0, waiters.length);
}

/** Learn only the proof's closed opaque cookie family, never diagnostic text. */
export function rememberBffCookies(headers, canaries) {
  for (const header of headers ?? []) {
    const pair = header.split(';', 1)[0];
    const equal = pair.indexOf('=');
    const name = pair.slice(0, equal);
    const value = pair.slice(equal + 1);
    if (!name.startsWith('__Host-ls-') || !value) continue;
    assert.match(value, /^[A-Za-z0-9_-]{43}$/);
    const transaction = /^(?:__Host-ls-login-|__Host-ls-logout-)([A-Za-z0-9_-]{43})$/.exec(name);
    assert.ok(
      transaction ||
        ['__Host-ls-session', '__Host-ls-browser', '__Host-ls-csrf', '__Host-ls-entry'].includes(
          name,
        ),
    );
    for (const secret of transaction ? [transaction[1], value] : [value]) {
      if (canaries.includes(secret)) continue;
      assert.ok(canaries.length < 1024, 'bounded proof cookie canaries');
      canaries.push(secret);
    }
  }
}
