import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

import { browserFailureDetail } from './admission-browser.mjs';
import { createPrivateScanner } from './fixture-support.mjs';
import { createBffShutdownValidator } from './framework-proof-relay.mjs';
import { assertUnforcedTestChildStop, stopTestChild } from './stop-test-child.mjs';

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

const stalledDevToolsStages = new Set([
  'browser-devtools-missing',
  'browser-devtools-empty',
  'browser-devtools-malformed',
]);

/**
 * A browser that stayed live through the whole readiness window without ever
 * publishing a usable port file never became controllable. Browser death, owner
 * closure and unreadable state are different facts and are never "stalls".
 */
export function bffDevToolsStalled(error) {
  return stalledDevToolsStages.has(bffDevToolsFailureStage(error));
}

/**
 * Start an owned browser and wait for its DevTools port, replacing a stalled
 * browser at most once.
 *
 * Hosted runners occasionally leave a live stock Chromium without a port file for
 * the whole window (observed in several unrelated fixtures; the cause is not
 * established). Browser launch is test tooling, not the behaviour under proof, so
 * one fresh process with a fresh profile is a legitimate infrastructure retry.
 * Only the proven stall signature qualifies, only after the stalled process is
 * stopped without force, and nothing after the DevTools port is ever retried. If
 * the stalled process cannot be stopped cleanly, its own closed failure stands and
 * no replacement starts; if the replacement also fails, that second closed
 * failure is the one reported.
 *
 * `launch(attempt)` must spawn through the fixture owner and return `{ child }`.
 * `wait(launched)` resolves with the port or rejects with the branded readiness
 * failure.
 */
export async function startBffBrowser({
  launch,
  wait,
  attempts = 2,
  stalled = bffDevToolsStalled,
  stop = stopTestChild,
}) {
  assert.ok(Number.isInteger(attempts) && attempts >= 1 && attempts <= 2, 'closed launch bound');
  for (let attempt = 1; ; attempt++) {
    const launched = launch(attempt);
    try {
      return { ...launched, port: await wait(launched) };
    } catch (error) {
      if (attempt >= attempts || !stalled(error)) throw error;
      try {
        assertUnforcedTestChildStop(launched.child, await stop(launched.child));
      } catch {
        throw error;
      }
    }
  }
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
      'session.extend',
      'session.extend_unsaved',
      'session.retry',
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

const listenerFailures = new WeakMap();

/** Return only observations minted by this helper, never arbitrary error fields. */
export function bffListenerFailureDetail(error) {
  return error !== null && typeof error === 'object' ? (listenerFailures.get(error) ?? null) : null;
}

/** Only the owned isolated protocol child can announce its physical listener. */
export function waitForBffProofListener(child) {
  const started = performance.now();
  let frameworkReason = 'unobserved';
  const refusal = (reason, message) => {
    const error = new Error(message);
    listenerFailures.set(
      error,
      Object.freeze({
        reason,
        elapsedMilliseconds: Math.min(120000, Math.max(0, Math.round(performance.now() - started))),
        exitCode:
          Number.isInteger(child.exitCode) && child.exitCode >= 0 && child.exitCode <= 255
            ? child.exitCode
            : null,
        signal:
          child.signalCode === null
            ? 'none'
            : ['SIGINT', 'SIGTERM', 'SIGKILL'].includes(child.signalCode)
              ? child.signalCode
              : 'other',
        frameworkReason,
      }),
    );
    return error;
  };
  return new Promise((resolve, reject) => {
    if (child.exitCode !== null || child.signalCode !== null) {
      reject(refusal('closed', 'native listener closed before readiness'));
      return;
    }
    let stderrOverlap = '';
    const reportedReasons = new Set([
      'owner_busy',
      'graph_identity',
      'development_identity',
      'development_compiler',
      'producer_failed',
    ]);
    const stderr = (chunk) => {
      const text = stderrOverlap + chunk.toString();
      if (frameworkReason === 'unobserved') {
        const match = /Framework admission refused: ([a-z_]{1,64})\r?\n/.exec(text);
        if (match) frameworkReason = reportedReasons.has(match[1]) ? match[1] : 'framework-other';
        else if (text.includes('Framework runtime admission failed\n'))
          frameworkReason = 'runtime-other';
      }
      // Credentials and arbitrary diagnostic text are never retained in the
      // observation. This bounded overlap exists only until readiness settles.
      stderrOverlap = text.slice(-512);
    };
    const timer = setTimeout(
      () => finish(refusal('deadline', 'listener startup deadline')),
      20_000,
    );
    function finish(error, port) {
      clearTimeout(timer);
      child.removeListener('message', ready);
      child.removeListener('close', closed);
      child.removeListener('error', failed);
      child.removeListener('child-error', failed);
      child.stderr?.removeListener('data', stderr);
      stderrOverlap = '';
      if (error) reject(error);
      else resolve(port);
    }
    function closed() {
      finish(refusal('closed', 'native listener closed before readiness'));
    }
    function failed() {
      finish(refusal('error', 'native listener startup failed'));
    }
    function ready(value) {
      if (
        !value ||
        Object.keys(value).sort().join(',') !== 'kind,port' ||
        value.kind !== 'proof-listener' ||
        !Number.isInteger(value.port) ||
        value.port < 1 ||
        value.port > 65535
      )
        finish(refusal('shape', 'owned listener readiness shape'));
      else finish(undefined, value.port);
    }
    child.stderr?.on('data', stderr);
    child.once('message', ready);
    child.once('close', closed);
    child.once('error', failed);
    child.once('child-error', failed);
  });
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
        [
          '__Host-ls-session',
          '__Host-ls-browser',
          '__Host-ls-csrf',
          '__Host-ls-entry',
          '__Host-ls-invitation',
        ].includes(name),
    );
    for (const secret of transaction ? [transaction[1], value] : [value]) {
      if (canaries.includes(secret)) continue;
      assert.ok(canaries.length < 1024, 'bounded proof cookie canaries');
      canaries.push(secret);
    }
  }
}

/** Scalar observation of the copied launcher's existing shutdown joins, never cleanup authority. */
export function observeBffProofShutdown(child) {
  const joins = ['listener', 'next', 'sessions', 'requests'];
  const validator = createBffShutdownValidator();
  let invalid = false;
  let closed = false;
  const observe = (value) => {
    // Other fixture IPC belongs to its existing owner, not this observation.
    if (value?.kind !== 'proof-shutdown') return;
    try {
      validator.observe(value);
    } catch {
      invalid = true;
    }
  };
  const close = () => {
    closed = true;
    child.removeListener('message', observe);
  };
  child.on('message', observe);
  child.once('close', close);
  const streamState = (stream) =>
    !stream ? 'absent' : stream.closed ? 'closed' : stream.readableEnded ? 'ended' : 'open';
  return Object.freeze({
    snapshot() {
      const { frames, state } = validator.snapshot();
      const joined = { pending: 'p', fulfilled: 'f', rejected: 'r' };
      return [
        invalid ? 'invalid' : frames ? 'valid' : 'absent',
        state.entered ? '1' : '0',
        state.watchdog ? '1' : '0',
        ...joins.map((key) => joined[state[key]]),
        child.signalCode !== null
          ? 'signal'
          : child.exitCode === null
            ? 'live'
            : child.exitCode === 0
              ? 'zero'
              : 'nonzero',
        closed ? '1' : '0',
        streamState(child.stdout),
        streamState(child.stderr),
      ].join(',');
    },
    sample() {
      return invalid ? 'invalid' : validator.snapshot().sample;
    },
  });
}

/** One owned cleanup, with only its first finite failure retained across retries. */
export function createBffBrowserCleanup(resources) {
  let pending;
  let failureStage = null;
  let nativeWait = null;
  let nativeSample = 'absent';
  const run = async () => {
    const { browser, browserProcess, nativeServer, owner } = resources();
    const failures = [];
    let browserStopped = false;
    let ownerDisposed = false;
    // Observe rejection immediately while preserving the actual close promise
    // for the final output/owner phase after group cleanup.
    const nativeClosed = nativeServer?.completed;
    void nativeClosed?.catch(() => {});
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
        // Join actual leader exit within the existing graceful window. Its
        // descendants may retain stdio until the unchanged group cleanup below;
        // child close belongs to the final output/owner phase, not this wait.
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
          let exited;
          try {
            const leader =
              child.exitCode !== null || child.signalCode !== null
                ? Promise.resolve({ code: child.exitCode, signal: child.signalCode })
                : new Promise((resolve) => {
                    exited = (code, signal) => resolve({ code, signal });
                    child.once('exit', exited);
                  });
            const stopped = await Promise.race([
              leader,
              new Promise((_, reject) => {
                timer = setTimeout(() => reject(new Error('BFF native shutdown deadline')), 5000);
              }),
            ]);
            if (stopped.signal !== null) failures.push('cleanup-native-signal');
            else if (stopped.code !== 0) failures.push('cleanup-native-exit');
          } catch {
            // Snapshot before fallback can change exit, close or stream observations.
            nativeWait = nativeServer.shutdown?.snapshot() ?? null;
            nativeSample = nativeServer.shutdown?.sample?.() ?? 'absent';
            failures.push('cleanup-native-wait');
          } finally {
            clearTimeout(timer);
            if (exited) child.removeListener('exit', exited);
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
    // Close and actual owner disposal share the existing five-second I/O phase.
    // No additional serial wait is introduced after the owner's output drain.
    let closeTimer;
    const [closed, disposed] = await Promise.allSettled([
      nativeClosed
        ? Promise.race([
            nativeClosed,
            new Promise((_, reject) => {
              closeTimer = setTimeout(() => reject(new Error('BFF native close deadline')), 5000);
            }),
          ])
        : undefined,
      Promise.resolve().then(() => owner.dispose()),
    ]);
    clearTimeout(closeTimer);
    // Append in deterministic lifecycle order, preserving an earlier failure.
    if (nativeClosed) {
      if (closed.status === 'rejected') {
        if (nativeWait === null) {
          nativeWait = nativeServer.shutdown?.snapshot() ?? null;
          nativeSample = nativeServer.shutdown?.sample?.() ?? 'absent';
        }
        failures.push('cleanup-native-wait');
      } else if (closed.value.signal !== null) failures.push('cleanup-native-signal');
      else if (closed.value.code !== 0) failures.push('cleanup-native-exit');
    }
    if (disposed.status === 'fulfilled') ownerDisposed = true;
    else failures.push('cleanup-owner');
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
    get nativeWait() {
      return nativeWait;
    },
    get nativeSample() {
      return nativeSample;
    },
  };
}
