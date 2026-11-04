// Closed genuine BFF transport proof. This driver owns one copied fixture.
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { existsSync, lstatSync, mkdtempSync, readFileSync, realpathSync, writeSync } from 'node:fs';
import { request } from 'node:https';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { gunzipSync, inflateSync } from 'node:zlib';

import { jar, startBffProof } from './bff-proof-host.mjs';
import { assertBffPrivateOutput } from './bff-proof-support.mjs';
import { createFixtureOwner } from './fixture-support.mjs';
import {
  copiedTransportEventsComplete,
  createCopiedTransportHost,
  transportProofFailureReason,
} from './framework-transport-proof-host.mjs';
import { stopTestChild } from './stop-test-child.mjs';

const owner = createFixtureOwner();
owner.install();
const root = owner.ownRoot(
  realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-framework-transport-'))),
);
const secret = randomBytes(32).toString('base64url');
const marker = randomBytes(32).toString('base64url');
const callbackCode = randomBytes(32).toString('base64url');
const privateValues = [secret, marker, callbackCode];
const hostName = 'institution.example.test:3000';
const pending = new Set();
let stage = 'arguments';
let running;
let proof;
// Only fixed stage names and the host-validated finite observations leave the fixture.
function progress(value) {
  try {
    writeSync(2, 'Framework transport evidence: ' + JSON.stringify(value) + '\n');
  } catch {
    /* Evidence is not cleanup authority. */
  }
}
function wireEvidence(name, response) {
  const encoding = response.headers['content-encoding'];
  progress({
    kind: 'wire',
    case: name,
    status: Number.isInteger(response.status) ? response.status : null,
    encoding:
      encoding === undefined ? null : ['gzip', 'deflate'].includes(encoding) ? encoding : 'other',
    bytes: response.body.length,
  });
}
function setStage(value) {
  stage = value;
  progress({ kind: 'stage', stage });
}
const deadline = setTimeout(() => {
  let observations = null;
  try {
    observations =
      proof?.events().map(({ case: name, kind, value }) => ({ case: name, kind, value })) ?? null;
  } catch {
    /* Invalid or unavailable observations never become success. */
  }
  progress({ kind: 'deadline', stage, observations });
  void owner.cancel();
}, 120_000);
deadline.unref();
function assertReleasedReservations(broker) {
  const records = broker.records();
  assert.ok(Array.isArray(records) && records.length === 3, 'three captured reservations');
  assert.ok(records.every((record) => typeof record?.path === 'string'));
  assert.equal(new Set(records.map((record) => record.path)).size, 3);
  for (const { path } of records) {
    try {
      lstatSync(path);
    } catch (error) {
      if (error?.code === 'ENOENT') continue;
      throw error;
    }
    throw new Error('Captured reservation remains');
  }
}
function own(promise) {
  pending.add(promise);
  void promise.then(
    () => pending.delete(promise),
    () => pending.delete(promise),
  );
  return promise;
}
async function login() {
  const get = running.get;
  setStage('login-csrf');
  const csrf = await own(get('/api/auth/csrf'));
  assert.equal(csrf.status, 200);
  const token = JSON.parse(csrf.body).csrfToken;
  assert.match(token, /^[A-Za-z0-9_-]{43}$/);
  privateValues.push(token);
  setStage('login-signin');
  const signIn = await own(
    get('/api/auth/signin/keycloak', jar(csrf), {
      method: 'POST',
      headers: {
        origin: 'https://' + hostName,
        'content-type': 'application/x-www-form-urlencoded',
      },
      body: new URLSearchParams({ csrfToken: token, returnTo: '/studio' }).toString(),
    }),
  );
  assert.equal(signIn.status, 303);
  const transaction = signIn.headers['set-cookie'].find((value) =>
    value.startsWith('__Host-ls-login-'),
  );
  assert.ok(transaction);
  const state = transaction.slice('__Host-ls-login-'.length).split('=')[0];
  privateValues.push(state);
  setStage('login-callback');
  const callback = await own(
    get('/api/auth/callback/keycloak?state=' + state + '&code=' + callbackCode, jar(signIn)),
  );
  assert.equal(callback.status, 303);
  assert.equal(callback.headers.location, 'https://' + hostName + '/api/auth/entry');
  assert.ok(callback.headers['set-cookie'].some((value) => value.startsWith('__Host-ls-entry=')));
  assert.ok(
    !callback.headers['set-cookie'].some((value) => value.startsWith('__Host-ls-session=')),
  );
  setStage('login-entry');
  const entry = await own(get('/api/auth/entry', jar(callback), { headers: { host: hostName } }));
  assert.equal(entry.status, 200);
  const form = /<form action="\/api\/auth\/entry\/select" method="post">([\s\S]*?)<\/form>/.exec(
    entry.body,
  );
  assert.ok(form);
  const entryToken = /name="csrfToken" value="([A-Za-z0-9_-]{43})"/.exec(form[1])?.[1];
  const tenantId = /name="tenantId" value="([0-9a-f-]{36})"/.exec(form[1])?.[1];
  assert.match(entryToken, /^[A-Za-z0-9_-]{43}$/);
  assert.match(tenantId, /^[0-9a-f-]{36}$/);
  privateValues.push(entryToken);
  setStage('login-select');
  const selected = await own(
    get('/api/auth/entry/select', jar(callback), {
      method: 'POST',
      headers: {
        host: hostName,
        origin: 'https://' + hostName,
        'content-type': 'application/x-www-form-urlencoded',
      },
      body: new URLSearchParams({ csrfToken: entryToken, tenantId }).toString(),
    }),
  );
  assert.equal(selected.status, 303);
  assert.equal(selected.headers.location, 'https://' + hostName + '/studio');
  assert.ok(selected.headers['set-cookie'].some((value) => value.startsWith('__Host-ls-session=')));
  return jar(selected);
}
function binaryPage(cookie, encoding) {
  return own(
    new Promise((resolve, reject) => {
      const call = request(
        {
          hostname: '127.0.0.1',
          port: running.listenerPort(),
          path: '/studio',
          ca: readFileSync(join(root, 'cert.pem')),
          servername: 'localhost',
          agent: false,
          headers: {
            host: hostName,
            cookie,
            accept: 'text/html',
            'sec-fetch-dest': 'document',
            'accept-encoding': encoding,
          },
        },
        (response) => {
          const chunks = [];
          let size = 0;
          response.on('data', (chunk) => {
            size += chunk.length;
            if (size > 2 * 1024 * 1024) {
              call.destroy(new Error('owned response body limit'));
              response.destroy();
            } else chunks.push(chunk);
          });
          response.once('error', reject);
          response.once('aborted', () => reject(new Error('owned response aborted')));
          response.once('end', () =>
            resolve({
              status: response.statusCode,
              headers: response.headers,
              body: Buffer.concat(chunks),
            }),
          );
        },
      );
      call.setTimeout(10_000, () => call.destroy(new Error('owned request deadline')));
      call.once('error', reject);
      call.end();
    }),
  );
}
async function completedEvents(proof, name) {
  let prior = '';
  for (;;) {
    try {
      owner.assertActive();
      assert.equal(running.server.process.exitCode, null, 'live owner during observation');
      assert.equal(running.server.process.signalCode, null, 'live owner during observation');
    } catch (error) {
      progress({ kind: 'fault', case: name, reason: 'owner-health' });
      throw error;
    }
    let rows;
    try {
      rows = proof.events().filter((row) => row.case === name);
    } catch (error) {
      progress({ kind: 'fault', case: name, reason: transportProofFailureReason(error) });
      throw error;
    }
    const observations = rows.map(({ kind, value }) => ({ kind, value }));
    const current = JSON.stringify(observations);
    if (current !== prior) {
      progress({ kind: 'observations', case: name, observations });
      prior = current;
    }
    try {
      if (copiedTransportEventsComplete(name, rows)) return rows;
    } catch (error) {
      progress({ kind: 'fault', case: name, reason: transportProofFailureReason(error) });
      throw error;
    }
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
}

function validateEvents(rows, name) {
  const one = (kind) => {
    const found = rows.filter((row) => row.kind === kind);
    assert.equal(found.length, 1, 'one actual ' + kind);
    return found[0];
  };
  one('router');
  one('handler-ok');
  one('response-finish');
  one('response-close');
  assert.ok(
    rows.findIndex((row) => row.kind === 'response-finish') <
      rows.findIndex((row) => row.kind === 'response-close'),
  );
  if (name === 'healthy-identity') assert.equal(one('none').value, 'not acceptable');
  else {
    assert.equal(one('allocation').value, name.endsWith('gzip') ? 'gzip' : 'deflate');
    assert.equal(one('encoder-close').value, name.startsWith('healthy-') ? 'ended' : 'unended');
    assert.ok(
      rows.findIndex((row) => row.kind === 'allocation') <
        rows.findIndex((row) => row.kind === 'encoder-close'),
    );
  }
}
try {
  assert.equal(process.argv.length, 2, 'no proof selectors');
  setStage('prepare');
  running = await startBffProof({
    owner,
    root,
    privateValues,
    secret,
    marker,
    uiMode: true,
    isolatedProtocol: true,
    transportProof: true,
    mark: (value) => {
      if (!['compile', 'build', 'start'].includes(value)) throw new Error('Fixture stage refused');
      setStage(value);
    },
  });
  proof = createCopiedTransportHost({ app: running.app, broker: running.server.broker });
  const family = running.server.broker.capture.family();
  assert.ok(family && typeof running.listenerPort === 'function');
  setStage('login');
  let cookie = await login();
  setStage('baseline');
  const baseline = await binaryPage(cookie, 'identity');
  wireEvidence('baseline', baseline);
  assert.equal(baseline.status, 200);
  assert.equal(baseline.headers['content-encoding'], undefined);
  assert.ok(baseline.body.length > 1024 && baseline.body.includes('data-bff-page-view="studio"'));
  const receipts = [];
  for (const name of [
    'healthy-identity',
    'healthy-gzip',
    'healthy-deflate',
    'fence-gzip',
    'fence-deflate',
  ]) {
    setStage(name);
    // A fenced response expires its real session; establish fresh authority for the next case.
    if (name === 'fence-deflate') {
      cookie = await login();
      setStage(name);
    }
    proof.arm(name);
    const encoding =
      name === 'healthy-identity' ? 'identity' : name.endsWith('gzip') ? 'gzip' : 'deflate';
    const result = await binaryPage(cookie, encoding);
    wireEvidence(name, result);
    const rows = await completedEvents(proof, name);
    progress({
      kind: 'observations',
      case: name,
      observations: rows.map(({ kind, value }) => ({ kind, value })),
    });
    validateEvents(rows, name);
    if (name.startsWith('healthy-')) {
      assert.equal(result.status, 200);
      if (encoding === 'identity') assert.equal(result.headers['content-encoding'], undefined);
      else assert.equal(result.headers['content-encoding'], encoding);
      const decoded =
        encoding === 'gzip'
          ? gunzipSync(result.body)
          : encoding === 'deflate'
            ? inflateSync(result.body)
            : result.body;
      assert.deepEqual(decoded, baseline.body, 'healthy body bytes preserved');
      assert.equal(result.headers['cache-control'], baseline.headers['cache-control']);
      assert.equal(result.headers['content-type'], baseline.headers['content-type']);
    } else {
      assert.equal(result.status, 503);
      assert.equal(result.headers['content-encoding'], undefined);
      assert.equal(result.headers['cache-control'], 'no-store');
      assert.equal(result.body.length, 0);
    }
    assert.equal(running.server.broker.capture.family(), family);
    receipts.push({
      case: name,
      status: result.status,
      events: rows.map(({ kind, value }) => ({ kind, value })),
    });
  }
  setStage('stop');
  const stopped = await stopTestChild(running.server.process);
  assert.equal(stopped.forced, false);
  assert.equal(stopped.ownerCode, 0);
  assert.equal(stopped.ownerSignal, null);
  assert.equal(running.server.broker.capture.retired(), true);
  const closed = await running.server.completed;
  assert.equal(closed.code, 0);
  assert.equal(closed.signal, null);
  running.server.broker.assertHealthy();
  assertReleasedReservations(running.server.broker);
  assertBffPrivateOutput(running.scanners);
  await owner.dispose();
  await Promise.allSettled([...pending]);
  assert.equal(existsSync(root), false);
  clearTimeout(deadline);
  process.stdout.write(
    JSON.stringify({ cases: receipts, familyRetired: true, privateCleanup: true }) + '\n',
  );
} catch {
  // Observe normal release before fixture disposal can remove either private path.
  let reservationsReleased = null;
  if (running) {
    try {
      const stopped = await stopTestChild(running.server.process);
      assert.equal(stopped.forced, false);
      assert.equal(stopped.ownerCode, 0);
      assert.equal(stopped.ownerSignal, null);
      assert.equal(running.server.broker.capture.retired(), true);
      assertReleasedReservations(running.server.broker);
      reservationsReleased = true;
    } catch {
      reservationsReleased = false;
    }
  }
  let disposal = 'settled';
  try {
    await owner.dispose();
  } catch {
    disposal = 'failed';
  }
  await Promise.allSettled([...pending]);
  let ownerStopped = null,
    familyRetired = null,
    checksPassed = null;
  if (running) {
    try {
      ownerStopped =
        running.server.process.exitCode !== null || running.server.process.signalCode !== null;
      familyRetired = running.server.broker.capture.retired();
      assert.equal(ownerStopped, true, 'failed run owner stopped');
      assert.equal(familyRetired, true, 'failed run captured family retired');
      assert.equal(reservationsReleased, true, 'failed run captured reservations released');
      checksPassed = true;
    } catch {
      checksPassed = false;
    }
  }
  const clean = disposal === 'settled' && checksPassed !== false;
  progress({ kind: 'cleanup', disposal, ownerStopped, familyRetired, checksPassed });
  clearTimeout(deadline);
  process.stderr.write('Framework transport proof failed: ' + stage + '\n');
  if (!clean) process.stderr.write('Framework transport cleanup failed.\n');
  process.exitCode = 1;
}
