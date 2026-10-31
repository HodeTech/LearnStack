import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

import { browserFailureDetail } from './admission-browser.mjs';
import { createPrivateScanner } from './fixture-support.mjs';
import { stopTestChild } from './stop-test-child.mjs';

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

/** One owned cleanup, with only its first finite failure retained across retries. */
export function createBffBrowserCleanup(resources) {
  let pending;
  let failureStage = null;
  const run = async () => {
    const { browser, browserProcess, nativeServer, owner } = resources();
    const failures = [];
    let browserStopped = false;
    let ownerDisposed = false;
    if (browser) {
      // CDP-connected browser.close() only disconnects Playwright. Request the
      // owned Chromium lifecycle first. All browser control steps share the
      // five-second control window; existing process bounds stay intact.
      const until = Date.now() + 5000;
      const bounded = async (operation) => {
        let timer;
        try {
          await Promise.race([
            Promise.resolve().then(operation),
            new Promise((_, reject) => {
              timer = setTimeout(
                () => reject(new Error('BFF browser control cleanup deadline')),
                Math.max(0, until - Date.now()),
              );
            }),
          ]);
        } finally {
          clearTimeout(timer);
        }
      };
      try {
        await bounded(async () => {
          const session = await browser.newBrowserCDPSession();
          await session.send('Browser.close');
        });
      } catch {
        failures.push('cleanup-cdp');
      }
      if (browserProcess) {
        let exited;
        try {
          await bounded(() => {
            if (browserProcess.exitCode !== null || browserProcess.signalCode !== null) return;
            return new Promise((resolve) => {
              exited = () => resolve();
              browserProcess.once('exit', exited);
            });
          });
        } catch {
          failures.push('cleanup-browser-wait');
        } finally {
          if (exited) browserProcess.removeListener('exit', exited);
        }
      }
      try {
        await bounded(() => browser.close());
      } catch {
        failures.push('cleanup-disconnect');
      }
    }
    for (const [kind, child] of [
      ['browser', browserProcess],
      ['native', nativeServer?.process],
    ]) {
      if (!child) continue;
      if (kind === 'native') {
        // Join the owned launcher's existing five-second shutdown before the
        // generic group fallback can escalate after two seconds. A completed
        // leader still needs the unchanged group-absence/no-forcing proof below.
        let requested = true;
        try {
          if (child.exitCode === null && child.signalCode === null && !child.kill('SIGTERM'))
            throw new Error('BFF native shutdown request failed');
        } catch {
          requested = false;
          failures.push('cleanup-native-shutdown');
        }
        if (requested) {
          let timer;
          try {
            const stopped = await Promise.race([
              nativeServer.completed,
              new Promise((_, reject) => {
                timer = setTimeout(() => reject(new Error('BFF native shutdown deadline')), 5000);
              }),
            ]);
            if (stopped.signal !== null) failures.push('cleanup-native-signal');
            else if (stopped.code !== 0) failures.push('cleanup-native-exit');
          } catch {
            failures.push('cleanup-native-wait');
          } finally {
            clearTimeout(timer);
          }
        }
      }
      try {
        const stopped = await stopTestChild(child);
        if (stopped.forced) failures.push('cleanup-' + kind + '-forced');
        else if (kind === 'browser') browserStopped = true;
      } catch {
        failures.push('cleanup-' + kind + '-stop');
      }
    }
    try {
      await owner.dispose();
      ownerDisposed = true;
    } catch {
      failures.push('cleanup-owner');
    }
    if (browserProcess) {
      if (browserProcess.signalCode !== null) failures.push('cleanup-browser-signal');
      else if (browserProcess.exitCode !== 0) failures.push('cleanup-browser-exit');
    }
    // Chromium may close the transport before acknowledging Browser.close.
    // Never infer success from an SDK error: require actual zero/signal-free
    // owned exit, group absence without forcing, and joined owner disposal.
    if (
      failures[0] === 'cleanup-cdp' &&
      browserProcess?.exitCode === 0 &&
      browserProcess.signalCode === null &&
      browserStopped &&
      ownerDisposed
    )
      failures.shift();
    if (failures.length) {
      failureStage = failures[0];
      throw new Error('BFF browser cleanup failed');
    }
  };
  return {
    run: () => (pending ??= run()),
    get failureStage() {
      return failureStage;
    },
  };
}
