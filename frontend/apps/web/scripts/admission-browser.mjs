// Test-only stock browser proof; no production route or runtime dependency.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash, X509Certificate } from 'node:crypto';
import { existsSync, mkdtempSync, readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { join } from 'node:path';

import { createPrivateScanner, fixtureEnvironment } from './fixture-support.mjs';
import { stopTestChild } from './stop-test-child.mjs';

const browserPhases = new Set([
  'browser-launch',
  'browser-devtools',
  'browser-discovery',
  'browser-protocol',
  'browser-navigation',
  'browser-hydration',
  'browser-arm',
  'browser-click',
  'browser-fallback',
  'browser-wire',
  'browser-completion',
  'browser-private-carrier',
  'browser-shutdown',
]);
const sanitizedFailures = new WeakSet();
const devToolsFailures = new WeakMap();
const devToolsKinds = new Set([
  'owner-inactive',
  'port-unreadable',
  'deadline-missing',
  'deadline-empty',
  'deadline-malformed',
]);

/** Only closed phase names may cross the private fixture's diagnostic boundary. */
export function browserFailureDetail(error) {
  if (!sanitizedFailures.has(error) || !browserPhases.has(error?.admissionStage)) return '';
  const kind =
    error.admissionStage === 'browser-devtools' ? devToolsFailures.get(error) : undefined;
  return ' ' + error.admissionStage + (devToolsKinds.has(kind) ? ' ' + kind : '');
}

function devToolsFailure(kind) {
  assert.ok(devToolsKinds.has(kind), 'Browser readiness diagnostic is registered');
  const failure = new Error('Public admission browser proof failed');
  failure.admissionStage = 'browser-devtools';
  sanitizedFailures.add(failure);
  devToolsFailures.set(failure, kind);
  return failure;
}

/** Never propagate raw browser errors, URLs, output or private fixture values. */
export async function atBrowserPhase(phase, operation) {
  assert.ok(browserPhases.has(phase), 'Browser diagnostic phase is registered');
  try {
    return await operation();
  } catch (error) {
    if (sanitizedFailures.has(error)) throw error;
    const failure = new Error('Public admission browser proof failed');
    failure.admissionStage = phase;
    sanitizedFailures.add(failure);
    throw failure;
  }
}

/** Cleanup always runs; its failure cannot replace an earlier proof failure. */
export async function withBrowserCleanup(operation, cleanup) {
  let failed = false;
  let failure;
  let result;
  try {
    result = await operation();
  } catch (error) {
    failed = true;
    failure = error;
  }
  try {
    await atBrowserPhase('browser-shutdown', cleanup);
  } catch (error) {
    if (!failed) throw error;
  }
  if (failed) throw failure;
  return result;
}

/** A failed Flight must precede a fresh successful document for the same target. */
export function assertFlightFallback(events, origin, target, status) {
  const matching = events.filter((event) => {
    const url = new URL(event.url);
    return url.origin === origin && url.pathname === target;
  });
  assert.equal(matching.length, 2, 'Exactly one Flight and one document navigation');
  const [flight, document] = matching;
  assert.equal(flight.rsc, true, 'The failed request is a genuine Flight');
  assert.notEqual(flight.type, 'Document', 'Flight cannot be a direct document');
  assert.equal(flight.status, status, 'Flight preserves the bootstrap HTTP refusal');
  assert.equal(document.rsc, false, 'Fallback is a fresh document request');
  assert.equal(document.type, 'Document', 'Stock Next performs document navigation');
  assert.equal(document.status, 200, 'The one-shot fault recovers on the new request');
}

function browserExecutable() {
  const candidates =
    process.platform === 'darwin'
      ? [
          '/Applications/Brave Browser.app/Contents/MacOS/Brave Browser',
          '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
        ]
      : [
          '/usr/bin/google-chrome',
          '/usr/bin/google-chrome-stable',
          '/usr/bin/chromium',
          '/usr/bin/chromium-browser',
        ];
  const binary = candidates.find(existsSync);
  assert.ok(binary, 'A stock Chromium browser is required; this proof cannot skip');
  return binary;
}

async function until(predicate, message, timeout = 20_000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    const value = await predicate();
    if (value) return value;
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
  throw new Error(message);
}

/** One read avoids a check/read race; neither bytes nor filesystem errors escape. */
export function readBrowserDevToolsPort(path) {
  let value;
  try {
    value = readFileSync(path, 'utf8');
  } catch (error) {
    return { kind: error?.code === 'ENOENT' ? 'missing' : 'unreadable' };
  }
  const first = value.split('\n', 1)[0];
  if (first.trim() === '') return { kind: 'empty' };
  if (!/^[1-9][0-9]{0,4}$/.test(first) || Number(first) > 65535) return { kind: 'malformed' };
  return { kind: 'ready', port: Number(first) };
}

/** Keep browser death and closed readiness observations distinct, with no extra wait. */
export function waitForBrowserDevTools({ assertActive, isLive, readPort }) {
  return atBrowserPhase('browser-devtools', async () => {
    const deadline = Date.now() + 20_000;
    let last = 'missing';
    const active = async () => {
      try {
        assertActive();
      } catch {
        throw devToolsFailure('owner-inactive');
      }
      await atBrowserPhase('browser-launch', () => {
        assert.equal(isLive(), true, 'The owned browser remains live');
      });
    };
    while (Date.now() < deadline) {
      await active();
      let observation;
      try {
        observation = readPort();
      } catch {
        throw devToolsFailure('port-unreadable');
      }
      if (
        observation?.kind === 'ready' &&
        Number.isInteger(observation.port) &&
        observation.port > 0 &&
        observation.port < 65536
      )
        return observation.port;
      if (!['missing', 'empty', 'malformed'].includes(observation?.kind))
        throw devToolsFailure('port-unreadable');
      last = observation.kind;
      await new Promise((resolve) => setTimeout(resolve, 50));
    }
    await active();
    throw devToolsFailure('deadline-' + last);
  });
}

/** Empty target lists may precede the page; all discovery shares the original 5 s. */
export function waitForBrowserPage({ assertActive, isLive, port }) {
  return atBrowserPhase('browser-discovery', async () => {
    const controller = new AbortController();
    let pollTimer;
    let deadlineTimer;
    const deadline = new Promise((_, reject) => {
      deadlineTimer = setTimeout(() => {
        const error = new Error('Owned browser discovery deadline');
        controller.abort(error);
        reject(error);
      }, 5000);
    });
    const assertBrowserActive = () => {
      assertActive();
      return atBrowserPhase('browser-launch', () => {
        assert.equal(isLive(), true, 'The owned browser remains live');
      });
    };
    const withinDeadline = async (operation) => {
      try {
        return await Promise.race([operation(), deadline]);
      } finally {
        await assertBrowserActive();
        controller.signal.throwIfAborted();
      }
    };
    try {
      while (true) {
        await assertBrowserActive();
        controller.signal.throwIfAborted();
        const response = await withinDeadline(() =>
          // eslint-disable-next-line no-restricted-globals -- Owned local browser CDP discovery, never a LearnStack API request.
          fetch('http://127.0.0.1:' + port + '/json/list', { signal: controller.signal }),
        );
        assert.equal(response.ok, true, 'Owned browser discovery succeeds');
        const pages = await withinDeadline(() => response.json());
        assert.ok(Array.isArray(pages), 'Owned browser exposes a target list');
        if (pages.length === 0) {
          await withinDeadline(
            () => new Promise((resolve) => (pollTimer = setTimeout(resolve, 50))),
          );
          continue;
        }
        const page = pages.find((candidate) => candidate.type === 'page');
        assert.ok(page, 'Owned browser exposes its initial page');
        const endpoint = new URL(page.webSocketDebuggerUrl);
        assert.equal(endpoint.protocol, 'ws:');
        assert.equal(endpoint.hostname, '127.0.0.1');
        assert.equal(endpoint.port, String(port));
        return endpoint;
      }
    } finally {
      clearTimeout(deadlineTimer);
      clearTimeout(pollTimer);
      controller.abort();
    }
  });
}

/** Isolated profile and certificate-specific trust; never touches a user's browser. */
export async function verifyBrowserFallback(options) {
  const phase = { value: 'browser-launch' };
  try {
    return await runBrowserFallback(options, phase);
  } catch (error) {
    if (sanitizedFailures.has(error)) throw error;
    return atBrowserPhase(phase.value, () => {
      throw error;
    });
  }
}

async function runBrowserFallback(
  {
    owner,
    app,
    root,
    certificate,
    origin,
    initialPath,
    target,
    status,
    beforeNavigation,
    afterNavigation,
    secrets,
  },
  phase,
) {
  const originUrl = new URL(origin);
  assert.equal(originUrl.protocol, 'https:');
  assert.equal(originUrl.origin, origin);
  assert.match(originUrl.hostname, /^[a-z0-9-]+(?:\.[a-z0-9-]+)+$/);
  const profile = mkdtempSync(join(root, 'admission-browser-profile-'));
  const fingerprint = createHash('sha256')
    .update(new X509Certificate(certificate).publicKey.export({ type: 'spki', format: 'der' }))
    .digest('base64');
  const child = owner.ownChild(
    spawn(
      browserExecutable(),
      [
        '--headless=new',
        '--no-first-run',
        '--no-default-browser-check',
        '--disable-background-networking',
        '--disable-component-update',
        '--disable-sync',
        '--disable-extensions',
        '--disable-breakpad',
        '--disable-dev-shm-usage',
        '--password-store=basic',
        '--use-mock-keychain',
        '--remote-debugging-address=127.0.0.1',
        '--remote-debugging-port=0',
        '--host-resolver-rules=MAP ' + originUrl.hostname + ' 127.0.0.1',
        '--user-data-dir=' + profile,
        '--ignore-certificate-errors-spki-list=' + fingerprint,
        'about:blank',
      ],
      {
        env: fixtureEnvironment({ root }),
        stdio: ['ignore', 'pipe', 'pipe'],
        detached: process.platform !== 'win32',
      },
    ),
  );
  const scanner = createPrivateScanner(secrets);
  for (const stream of [child.stdout, child.stderr]) {
    stream.on('data', (chunk) => scanner.push(chunk));
    stream.once('end', () => scanner.finish());
  }
  let failed = false;
  child.once('error', () => {
    failed = true;
  });
  const portFile = join(profile, 'DevToolsActivePort');
  phase.value = 'browser-devtools';
  const port = await waitForBrowserDevTools({
    assertActive: () => owner.assertActive(),
    isLive: () => !failed && child.exitCode === null && child.signalCode === null,
    readPort: () => readBrowserDevToolsPort(portFile),
  });
  phase.value = 'browser-discovery';
  const endpoint = await waitForBrowserPage({
    assertActive: () => owner.assertActive(),
    isLive: () => !failed && child.exitCode === null && child.signalCode === null,
    port,
  });
  const WebSocket = createRequire(join(app, 'package.json'))('next/dist/compiled/ws');
  const socket = new WebSocket(endpoint.href, { maxPayload: 2 * 1024 * 1024 });
  const pending = new Map();
  const events = [];
  const requests = new Map();
  let sequence = 0;
  const rejectPending = () => {
    for (const operation of pending.values()) {
      clearTimeout(operation.timer);
      operation.reject(new Error('Browser protocol closed'));
    }
    pending.clear();
  };
  socket.on('error', rejectPending);
  socket.on('close', rejectPending);
  const command = (method, params = {}) =>
    new Promise((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => {
        pending.delete(id);
        reject(new Error('Browser protocol deadline'));
      }, 10_000);
      pending.set(id, { resolve, reject, timer });
      socket.send(JSON.stringify({ id, method, params }));
    });
  socket.on('message', (value) => {
    const message = JSON.parse(value.toString());
    if (message.id !== undefined) {
      const operation = pending.get(message.id);
      if (!operation) return;
      clearTimeout(operation.timer);
      pending.delete(message.id);
      if (message.error) operation.reject(new Error('Browser protocol refused'));
      else operation.resolve(message.result);
      return;
    }
    if (message.method === 'Network.requestWillBeSent') {
      const { requestId, request, type } = message.params;
      if (events.length >= 256) return;
      const event = {
        url: request.url,
        type,
        rsc: Object.entries(request.headers).some(
          ([key, value]) => key.toLowerCase() === 'rsc' && value === '1',
        ),
        status: null,
      };
      requests.set(requestId, event);
      events.push(event);
    }
    if (message.method === 'Network.responseReceived') {
      const event = requests.get(message.params.requestId);
      if (event) event.status = message.params.response.status;
    }
  });
  const evaluateBoolean = async (expression) => {
    try {
      const result = await command('Runtime.evaluate', { expression, returnByValue: true });
      return result.result.value === true;
    } catch {
      // A genuine document navigation replaces its execution context. Poll the
      // new context within the fixed deadline; network observations stay mandatory.
      return false;
    }
  };
  return withBrowserCleanup(
    async () => {
      phase.value = 'browser-protocol';
      await new Promise((resolve, reject) => {
        const timer = setTimeout(
          () => reject(new Error('Browser protocol startup deadline')),
          5000,
        );
        socket.once('open', () => {
          clearTimeout(timer);
          resolve();
        });
        socket.once('error', () => {
          clearTimeout(timer);
          reject(new Error('Browser protocol startup'));
        });
      });
      await command('Network.enable');
      await command('Page.enable');
      await command('Runtime.enable');
      phase.value = 'browser-navigation';
      await command('Page.navigate', { url: origin + initialPath });
      phase.value = 'browser-hydration';
      await until(
        () => evaluateBoolean('Boolean(document.querySelector(\'[data-admission-ready="true"]\'))'),
        'Navigation probe hydration deadline',
      );
      phase.value = 'browser-arm';
      await beforeNavigation();
      events.length = 0;
      requests.clear();
      phase.value = 'browser-click';
      await command('Runtime.evaluate', {
        expression: "document.querySelector('[data-admission-navigation]').click()",
      });
      phase.value = 'browser-fallback';
      await until(async () => {
        const complete = await evaluateBoolean(
          'location.pathname === ' +
            JSON.stringify(target) +
            ' && document.readyState === "complete" && Boolean(document.querySelector("h1"))',
        );
        return (
          complete &&
          events.some(
            (event) =>
              event.type === 'Document' &&
              new URL(event.url).pathname === target &&
              event.status === 200,
          )
        );
      }, 'Stock Next document fallback deadline');
      phase.value = 'browser-wire';
      assertFlightFallback(events, origin, target, status);
      phase.value = 'browser-completion';
      await afterNavigation();
      await atBrowserPhase('browser-private-carrier', () => {
        assert.equal(scanner.leaked, false, 'Browser diagnostics contain no private carrier');
      });
      return scanner;
    },
    async () => {
      rejectPending();
      socket.terminate();
      await stopTestChild(child);
      scanner.finish();
      await atBrowserPhase('browser-private-carrier', () => {
        assert.equal(
          scanner.leaked,
          false,
          'Browser shutdown diagnostics contain no private carrier',
        );
      });
    },
  );
}
