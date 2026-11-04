// Closed genuine BFF transport proof. This driver owns one copied fixture.
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { existsSync, lstatSync, mkdtempSync, readFileSync, realpathSync, writeSync } from 'node:fs';
import { request } from 'node:https';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { jar, startBffProof } from './bff-proof-host.mjs';
import { assertBffPrivateOutput } from './bff-proof-support.mjs';
import { createFixtureOwner } from './fixture-support.mjs';
import { createCopiedOverlapHost } from './framework-overlap-proof-host.mjs';
import { assertCompletedOverlap } from './framework-overlap-proof-rows.mjs';
import { createCopiedTransportHost } from './framework-transport-proof-host.mjs';
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
let overlap;
// Only fixed stage names and the host-validated finite observations leave the fixture.
function progress(value) {
  try {
    writeSync(2, 'Framework transport evidence: ' + JSON.stringify(value) + '\n');
  } catch {
    /* Evidence is not cleanup authority. */
  }
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
// A request is destroyed only after an actual gzip response delivers actual bytes.
function destroyAfterActualGzip(cookie) {
  return own(
    new Promise((resolve, reject) => {
      let responseSeen = false,
        responseClosed = false,
        requestClosed = false,
        intentionallyClosed = false,
        failed = false,
        done = false;
      let ownedResponse;
      let status = null,
        encoding = null,
        bytes = 0;
      const complete = () => {
        if (!done && responseClosed && requestClosed) {
          done = true;
          if (failed) reject(new Error('Owned overlap client failed'));
          else resolve({ status, encoding, bytes });
        }
      };
      const fail = () => {
        failed = true;
        ownedResponse?.destroy();
        call.destroy();
        if (!responseSeen) responseClosed = true;
        complete();
      };
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
            'accept-encoding': 'gzip',
          },
        },
        (response) => {
          responseSeen = true;
          ownedResponse = response;
          status = response.statusCode;
          encoding = response.headers['content-encoding'];
          response.once('error', () => {
            if (!intentionallyClosed) fail();
          });
          response.once('aborted', () => {
            if (!intentionallyClosed) fail();
          });
          response.once('end', () => {
            if (!intentionallyClosed) fail();
          });
          response.once('close', () => {
            responseClosed = true;
            complete();
          });
          if (status !== 200 || encoding !== 'gzip') {
            fail();
            return;
          }
          response.once('data', (chunk) => {
            bytes = chunk.length;
            if (bytes < 1) {
              fail();
              return;
            }
            intentionallyClosed = true;
            response.destroy();
            call.destroy();
          });
        },
      );
      call.once('error', () => {
        if (!intentionallyClosed) fail();
      });
      call.once('close', () => {
        requestClosed = true;
        if (!responseSeen) fail();
        else complete();
      });
      call.setTimeout(10_000, () => call.destroy(new Error('owned request deadline')));
      call.end();
    }),
  );
}
async function awaitRows(predicate) {
  for (;;) {
    owner.assertActive();
    running.server.broker.assertHealthy();
    assert.equal(running.server.process.exitCode, null);
    assert.equal(running.server.process.signalCode, null);
    const transport = proof.events(),
      rows = overlap.events();
    if (transport.some((row) => row.case === 'healthy-gzip' && row.kind === 'none'))
      throw new Error('encoder not allocated');
    if (
      rows.some((row) =>
        ['api-fulfilled', 'api-rejected', 'bounded-b-fulfilled', 'bounded-b-rejected'].includes(
          row.event,
        ),
      ) &&
      !rows.some((row) => row.event === 'release-api')
    )
      throw new Error('fixture operation completed early');
    if (predicate(rows, transport)) return { rows, transport };
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
}
async function readyForRelease(index) {
  for (;;) {
    await running.renderControl('status');
    const { rows } = await awaitRows((rows) =>
      rows.some((row) => row.event === 'snapshot' && row.state.releaseIndex === index),
    );
    const state = rows.filter((row) => row.event === 'snapshot').at(-1).state;
    if (state.releaseIndex !== index || state.diagnosticFault || state.stop !== 'pending')
      throw new Error('overlap phase refused');
    if (state.stageAcknowledged) return;
    await new Promise((resolve) => setTimeout(resolve, 10));
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
    overlapProof: true,
    mark: (value) => {
      if (!['compile', 'build', 'start'].includes(value)) throw new Error('Fixture stage refused');
      setStage(value);
    },
  });
  proof = createCopiedTransportHost({ app: running.app, broker: running.server.broker });
  overlap = createCopiedOverlapHost({ app: running.app, broker: running.server.broker });
  const family = running.server.broker.capture.family();
  assert.ok(family);
  setStage('login-b');
  const cookieB = await login();
  setStage('login-a');
  const cookieA = await login();
  assert.notEqual(cookieA, cookieB, 'separate actual fixture sessions');
  setStage('arm');
  await running.renderControl('arm');
  setStage('request-b');
  const bRequest = own(
    running.get('/studio', cookieB, {
      headers: { host: hostName, accept: 'text/html', 'sec-fetch-dest': 'document' },
    }),
  );
  void bRequest.catch(() => {});
  await awaitRows((rows) =>
    ['reserve-b', 'api-entered', 'api-tracked', 'bounded-b-tracked'].every((event) =>
      rows.some((row) => row.event === event),
    ),
  );
  setStage('request-a');
  proof.arm('healthy-gzip');
  const aWire = await destroyAfterActualGzip(cookieA);
  assert.equal(aWire.status, 200);
  assert.equal(aWire.encoding, 'gzip');
  assert.ok(aWire.bytes > 0);
  const observed = await awaitRows(
    (rows, transport) =>
      rows.some((row) => row.event === 'response-closed') &&
      rows.some((row) => row.event === 'encoder-closed') &&
      transport.some(
        (row) => row.case === 'healthy-gzip' && row.kind === 'allocation' && row.value === 'gzip',
      ) &&
      transport.some((row) => row.case === 'healthy-gzip' && row.kind === 'encoder-close') &&
      transport.some((row) => row.case === 'healthy-gzip' && row.kind === 'response-close'),
  );
  assert.equal(
    observed.transport.filter((row) => row.case === 'healthy-gzip' && row.kind === 'allocation')
      .length,
    1,
  );
  assert.equal(
    observed.transport.filter((row) => row.case === 'healthy-gzip' && row.kind === 'encoder-close')
      .length,
    1,
  );
  setStage('native-stop');
  await running.renderControl('release');
  for (let phase = 0; phase < 4; phase++) {
    setStage(['release-render', 'release-framework', 'release-snapshot', 'release-api'][phase]);
    await readyForRelease(phase);
    await running.renderControl('release');
  }
  const completed = await awaitRows((rows) =>
    rows.some((row) => row.event === 'native-stop-fulfilled'),
  );
  const result = assertCompletedOverlap(completed.rows);
  const events = completed.rows.map(({ event, state }) => ({ event, state }));
  const transport = completed.transport
    .filter((row) => row.case === 'healthy-gzip')
    .map(({ kind, value }) => ({ kind, value }));
  assert.equal(running.server.broker.capture.family(), family);
  await Promise.allSettled([bRequest]);
  progress({ kind: 'overlap', events, transport });
  overlap.close();
  overlap = undefined;
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
    JSON.stringify({ result, events, transport, familyRetired: true, privateCleanup: true }) + '\n',
  );
} catch {
  try {
    const rows = overlap?.events() ?? null,
      transport = proof?.events() ?? null;
    progress({
      kind: 'observations',
      events: rows?.map(({ event, state }) => ({ event, state })) ?? null,
      transport:
        transport
          ?.filter((row) => row.case === 'healthy-gzip')
          .map(({ kind, value }) => ({ kind, value })) ?? null,
    });
  } catch {
    /* Refused bytes are never projected as evidence. */
  }
  try {
    overlap?.close();
  } catch {
    /* refusal never grants cleanup credit */
  }
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
      assert.equal(ownerStopped, true);
      assert.equal(familyRetired, true);
      assert.equal(reservationsReleased, true);
      checksPassed = true;
    } catch {
      checksPassed = false;
    }
  }
  progress({ kind: 'cleanup', disposal, ownerStopped, familyRetired, checksPassed });
  clearTimeout(deadline);
  process.stderr.write('Framework overlap proof failed: ' + stage + '\n');
  process.exitCode = 1;
}
