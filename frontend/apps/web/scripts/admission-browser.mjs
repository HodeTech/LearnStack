// Test-only stock browser proof; no production route or runtime dependency.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash, X509Certificate } from 'node:crypto';
import { existsSync, mkdtempSync, readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { join } from 'node:path';

import { createPrivateScanner, fixtureEnvironment } from './fixture-support.mjs';
import { stopTestChild } from './stop-test-child.mjs';

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

/** Isolated profile and certificate-specific trust; never touches a user's browser. */
export async function verifyBrowserFallback({
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
}) {
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
  const port = await until(() => {
    owner.assertActive();
    assert.equal(
      failed || child.exitCode !== null || child.signalCode !== null,
      false,
      'The owned browser remains live',
    );
    if (!existsSync(portFile)) return null;
    const value = Number(readFileSync(portFile, 'utf8').split('\n')[0]);
    return Number.isInteger(value) && value > 0 && value < 65536 ? value : null;
  }, 'Owned browser debugging deadline');
  // eslint-disable-next-line no-restricted-globals -- Owned local browser CDP discovery, never a LearnStack API request.
  const response = await fetch('http://127.0.0.1:' + port + '/json/list', {
    signal: AbortSignal.timeout(5000),
  });
  const pages = await response.json();
  const page = pages.find((candidate) => candidate.type === 'page');
  assert.ok(page, 'Owned browser exposes its initial page');
  const endpoint = new URL(page.webSocketDebuggerUrl);
  assert.equal(endpoint.hostname, '127.0.0.1');
  assert.equal(endpoint.port, String(port));
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
  try {
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('Browser protocol startup deadline')), 5000);
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
    await command('Page.navigate', { url: origin + initialPath });
    await until(
      () => evaluateBoolean('Boolean(document.querySelector(\'[data-admission-ready="true"]\'))'),
      'Navigation probe hydration deadline',
    );
    await beforeNavigation();
    events.length = 0;
    requests.clear();
    await command('Runtime.evaluate', {
      expression: "document.querySelector('[data-admission-navigation]').click()",
    });
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
    assertFlightFallback(events, origin, target, status);
    await afterNavigation();
    assert.equal(scanner.leaked, false, 'Browser diagnostics contain no private carrier');
    return scanner;
  } finally {
    rejectPending();
    socket.terminate();
    await stopTestChild(child);
    scanner.finish();
    assert.equal(scanner.leaked, false, 'Browser shutdown diagnostics contain no private carrier');
  }
}
