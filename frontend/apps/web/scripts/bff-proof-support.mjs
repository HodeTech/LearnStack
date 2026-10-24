import assert from 'node:assert/strict';

import { createPrivateScanner } from './fixture-support.mjs';

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
      transaction || ['__Host-ls-session', '__Host-ls-browser', '__Host-ls-csrf'].includes(name),
    );
    for (const secret of transaction ? [transaction[1], value] : [value]) {
      if (canaries.includes(secret)) continue;
      assert.ok(canaries.length < 1024, 'bounded proof cookie canaries');
      canaries.push(secret);
    }
  }
}
