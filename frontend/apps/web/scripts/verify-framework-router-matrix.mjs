// Closed first App Router HTTP/HTTPS probe on one copied fixture.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { existsSync, lstatSync, mkdtempSync, realpathSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { performance } from 'node:perf_hooks';

import { startBffProof } from './bff-proof-host.mjs';
import { assertBffPrivateOutput } from './bff-proof-support.mjs';
import { createFixtureOwner } from './fixture-support.mjs';
import {
  describeRouterParser,
  deriveRouterPipeUnion,
  routerPipeInventory,
} from './framework-router-generated-pipes.mjs';
import { createCopiedRouterHttpPort } from './framework-router-matrix-port-host.mjs';
import {
  copiedRoutePairs,
  requestCopiedRoute,
  validateCopiedRouteEvents,
  validateCopiedRouteWire,
} from './framework-router-matrix-probe-client.mjs';
import { deriveRouterPipeSpec } from './framework-router-matrix-source.mjs';
import {
  createCopiedTransportHost,
  copiedTransportEventsComplete,
} from './framework-transport-proof-host.mjs';
import { deriveTransportSpec } from './framework-transport-proof-source.mjs';
import { stopTestChild } from './stop-test-child.mjs';
import { verifyFrameworkInstallation } from '../../../scripts/framework-installation.mjs';

const owner = createFixtureOwner();
owner.install();
const root = owner.ownRoot(realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-router-matrix-'))));
const secret = randomBytes(32).toString('base64url');
const marker = randomBytes(32).toString('base64url');
const privateValues = [secret, marker];
const pending = new Set();
let stage = 'start';
let running;
let portHost;
let proof;
let launchRequestedAt;
let launchRequestToReadyMs;
let processSample;
let lastWire = null;
let lastTransportRows = null;
let lastPipeRows = null;
let stopAttempted = false;
let stopResult = null;
let ownerClose = null;
let familyRetired = null;
let reservationChecks = null;
const fixtureStartedAt = performance.now();
function projectWire(result) {
  const headers = result?.headers ?? {};
  const category = (value, exact) =>
    value === undefined ? 'absent' : value === exact ? exact : 'other';
  return Object.freeze({
    status:
      Number.isSafeInteger(result?.status) && result.status >= 100 && result.status <= 599
        ? result.status
        : null,
    encoding: ['gzip', 'deflate', 'identity'].includes(headers['content-encoding'])
      ? headers['content-encoding']
      : category(headers['content-encoding'], 'absent'),
    bodyBytes: Buffer.isBuffer(result?.body) ? result.body.length : null,
    cacheControl: category(headers['cache-control'], 'no-store'),
    contentType:
      typeof headers['content-type'] === 'string' &&
      /^text\/plain(?:; charset=utf-8)?$/i.test(headers['content-type'])
        ? 'text-plain'
        : headers['content-type'] === undefined
          ? 'absent'
          : 'other',
    tlsAuthorized: result?.authorized === true ? true : result?.authorized === false ? false : null,
  });
}
function observeReservations(broker) {
  try {
    const records = broker.records();
    if (!Array.isArray(records))
      return { count: null, distinct: null, frontend: null, app: null, sdk: null };
    const status = (record) => {
      if (!record || typeof record.path !== 'string') return 'unknown';
      try {
        lstatSync(record.path);
        return 'present';
      } catch (error) {
        return error?.code === 'ENOENT' ? 'absent' : 'unknown';
      }
    };
    return Object.freeze({
      count: records.length,
      distinct: new Set(records.map((record) => record.path)).size === records.length,
      frontend: records.length === 3 ? status(records[0]) : 'unknown',
      app: records.length === 3 ? status(records[1]) : 'unknown',
      sdk: records.length === 3 ? status(records[2]) : 'unknown',
    });
  } catch {
    return { count: null, distinct: null, frontend: 'unknown', app: 'unknown', sdk: 'unknown' };
  }
}
function closedSignal(value) {
  return value === null ? null : ['SIGTERM', 'SIGINT', 'SIGKILL'].includes(value) ? value : 'other';
}
function projectStop(stopped) {
  return Object.freeze({
    forced: typeof stopped.forced === 'boolean' ? stopped.forced : null,
    ownerCode: Number.isSafeInteger(stopped.ownerCode) ? stopped.ownerCode : null,
    ownerSignal: closedSignal(stopped.ownerSignal),
  });
}
function sampleOwnedProcesses(child, broker) {
  broker.assertHealthy();
  assert.equal(child.exitCode, null);
  assert.equal(child.signalCode, null);
  const ownerPid = child.pid;
  const family = broker.capture.family();
  const lease = broker.capture.lease();
  assert.ok(family && Object.isFrozen(family) && lease && Object.isFrozen(lease));
  const runtimePid = family.pid;
  assert.ok(Number.isSafeInteger(ownerPid) && ownerPid > 0);
  assert.ok(Number.isSafeInteger(runtimePid) && runtimePid > 0 && runtimePid !== ownerPid);
  assert.equal(lease.pid, ownerPid);
  assert.equal(family.parentPid, ownerPid);
  assert.equal(family.group, runtimePid);
  let raw;
  try {
    raw = execFileSync(
      'ps',
      ['-p', `${ownerPid},${runtimePid}`, '-o', 'pid=', '-o', 'ppid=', '-o', 'pgid=', '-o', 'rss='],
      { encoding: 'utf8', timeout: 1000, maxBuffer: 512, stdio: ['ignore', 'pipe', 'ignore'] },
    );
  } catch {
    throw new Error('Owned runtime process sample refused');
  }
  broker.assertHealthy();
  assert.equal(child.exitCode, null);
  assert.equal(child.signalCode, null);
  assert.equal(broker.capture.family(), family);
  assert.equal(broker.capture.lease(), lease);
  const lines = raw.trim().split('\n');
  assert.equal(lines.length, 2, 'Exact owner and runtime process sample');
  const rows = new Map();
  for (const line of lines) {
    const match = /^\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*$/.exec(line);
    assert.ok(match, 'Closed process sample shape');
    const values = match.slice(1).map(Number);
    assert.ok(values.every((value) => Number.isSafeInteger(value) && value >= 0));
    assert.equal(rows.has(values[0]), false, 'One row per exact process');
    rows.set(values[0], { ppid: values[1], pgid: values[2], rssKiB: values[3] });
  }
  assert.equal(rows.size, 2);
  assert.ok(rows.has(ownerPid) && rows.has(runtimePid));
  assert.equal(rows.get(runtimePid).ppid, ownerPid);
  assert.equal(rows.get(runtimePid).pgid, runtimePid);
  assert.ok(rows.get(ownerPid).rssKiB > 0 && rows.get(runtimePid).rssKiB > 0);
  return Object.freeze({
    ownerPid,
    runtimePid,
    ownerRssKiB: rows.get(ownerPid).rssKiB,
    runtimeRssKiB: rows.get(runtimePid).rssKiB,
  });
}
function own(promise) {
  pending.add(promise);
  void promise.then(
    () => pending.delete(promise),
    () => pending.delete(promise),
  );
  return promise;
}
function assertReleasedReservations(broker) {
  const records = broker.records();
  assert.ok(Array.isArray(records) && records.length === 3);
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
async function completed(name) {
  for (;;) {
    owner.assertActive();
    assert.equal(running.server.process.exitCode, null);
    assert.equal(running.server.process.signalCode, null);
    portHost.assertBinding();
    const rows = proof.events().filter((row) => row.case === name);
    lastTransportRows = Object.freeze({
      count: rows.length,
      tail: Object.freeze(rows.slice(-12)),
    });
    if (copiedTransportEventsComplete(name, rows)) return rows;
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
}
const deadline = setTimeout(() => void owner.cancel(), 120_000);
deadline.unref();
try {
  assert.equal(process.argv.length, 2, 'No matrix selectors');
  running = await startBffProof({
    owner,
    root,
    privateValues,
    secret,
    marker,
    uiMode: true,
    isolatedProtocol: true,
    transportProof: true,
    routerMatrix: true,
    mark(phase) {
      assert.ok(['compile', 'build', 'start'].includes(phase), 'Closed fixture phase');
      if (phase === 'start') {
        assert.equal(launchRequestedAt, undefined, 'One native launch request');
        launchRequestedAt = performance.now();
      }
    },
  });
  assert.ok(Number.isFinite(launchRequestedAt), 'Native launch request observed');
  launchRequestToReadyMs = Math.round(performance.now() - launchRequestedAt);
  assert.ok(Number.isSafeInteger(launchRequestToReadyMs) && launchRequestToReadyMs >= 0);
  stage = 'captured-family';
  void running.server.completed.then(
    (closed) => {
      ownerClose = { code: closed.code, signal: closedSignal(closed.signal) };
    },
    () => {
      ownerClose = { code: null, signal: 'other' };
    },
  );
  const family = running.server.broker.capture.family();
  processSample = sampleOwnedProcesses(running.server.process, running.server.broker);
  proof = createCopiedTransportHost({
    app: running.app,
    broker: running.server.broker,
  });
  const identity = verifyFrameworkInstallation({ appRoot: running.app });
  const pipeUnion = deriveRouterPipeUnion(
    Object.freeze({ identity, mode: 'production' }),
    deriveRouterPipeSpec(identity),
    describeRouterParser(identity),
  );
  const inventory = routerPipeInventory(pipeUnion);
  const compressionSha256 = deriveTransportSpec(identity).compression.sha256;
  portHost = createCopiedRouterHttpPort({
    app: running.app,
    broker: running.server.broker,
    union: pipeUnion,
    compressionSha256,
  });
  const rows = [];
  for (const { transport, encoding, name } of copiedRoutePairs) {
    lastWire = null;
    lastTransportRows = null;
    lastPipeRows = null;
    stage = name + ':arm';
    proof.arm(name);
    stage = name + ':request';
    const result = await own(
      requestCopiedRoute({
        root,
        transport,
        name,
        port: transport === 'https' ? running.listenerPort() : portHost.port,
        encoding,
      }),
    );
    lastWire = projectWire(result);
    stage = name + ':wire-validation';
    const wire = validateCopiedRouteWire(result, name);
    stage = name + ':event-wait';
    const events = await completed(name);
    stage = name + ':pipe-validation';
    const pipeRows = portHost.pipeEvents();
    lastPipeRows = Object.freeze({
      count: pipeRows.length,
      tail: Object.freeze(pipeRows.slice(-3)),
    });
    const pipe = validateCopiedRouteEvents({
      rows: events,
      pipeRows,
      name,
      pipeUnion,
    });
    portHost.assertCase(name);
    assert.equal(running.server.broker.capture.family(), family);
    rows.push({ ...wire, ...pipe });
  }
  stage = 'stop';
  stopAttempted = true;
  const stopped = await stopTestChild(running.server.process);
  stopResult = projectStop(stopped);
  assert.equal(stopped.forced, false);
  assert.equal(stopped.ownerCode, 0);
  assert.equal(stopped.ownerSignal, null);
  familyRetired = running.server.broker.capture.retired();
  assert.equal(familyRetired, true);
  const closed = await running.server.completed;
  ownerClose = { code: closed.code, signal: closedSignal(closed.signal) };
  assert.equal(closed.code, 0);
  assert.equal(closed.signal, null);
  running.server.broker.assertHealthy();
  reservationChecks = observeReservations(running.server.broker);
  assertReleasedReservations(running.server.broker);
  assertBffPrivateOutput(running.scanners);
  const finalPipe = portHost.assertClosed();
  await owner.dispose();
  await Promise.allSettled([...pending]);
  assert.equal(existsSync(root), false);
  clearTimeout(deadline);
  const fixtureTotalMs = Math.round(performance.now() - fixtureStartedAt);
  assert.ok(Number.isSafeInteger(fixtureTotalMs) && fixtureTotalMs >= launchRequestToReadyMs);
  process.stdout.write(
    JSON.stringify({
      cases: rows,
      pipeProof: {
        manifestSha256: inventory.manifestSha256,
        inventorySha256: inventory.sha256,
        installedFiles: 27,
        generatedFiles: pipeUnion.files.length - 27,
        sites: pipeUnion.sites.length,
        tracedEntries: finalPipe.attestations.length,
        filteredBaseline: finalPipe.filters.length === 1,
      },
      familyRetired: true,
      privateCleanup: true,
      measurements: { launchRequestToReadyMs, fixtureTotalMs, ...processSample },
    }) + '\n',
  );
} catch {
  let released = false;
  if (running) {
    try {
      stopAttempted = true;
      const stopped = await stopTestChild(running.server.process);
      stopResult = projectStop(stopped);
      assert.equal(stopped.forced, false);
      assert.equal(stopped.ownerCode, 0);
      assert.equal(stopped.ownerSignal, null);
      familyRetired = running.server.broker.capture.retired();
      assert.equal(familyRetired, true);
      reservationChecks = observeReservations(running.server.broker);
      assertReleasedReservations(running.server.broker);
      released = true;
    } catch {
      /* Unknown family or reservation remains a failure. */
    }
    try {
      familyRetired = running.server.broker.capture.retired();
    } catch {
      familyRetired = null;
    }
    reservationChecks = observeReservations(running.server.broker);
  }
  let disposed = false;
  try {
    await owner.dispose();
    disposed = true;
  } catch {
    /* Retain uncertain private roots. */
  }
  await Promise.allSettled([...pending]);
  clearTimeout(deadline);
  process.stderr.write('Copied router matrix failed: ' + stage + '\n');
  if (!released || !disposed) process.stderr.write('Copied router matrix cleanup failed.\n');
  process.stderr.write(
    'Copied router matrix observation: ' +
      JSON.stringify({
        stage,
        wire: lastWire,
        transport: lastTransportRows,
        pipe: lastPipeRows,
        stopAttempted,
        stopResult,
        ownerClose,
        ownerProcess: running
          ? {
              exitCode: running.server.process.exitCode,
              signalCode: closedSignal(running.server.process.signalCode),
            }
          : null,
        familyRetired,
        reservations: reservationChecks,
      }) +
      '\n',
  );
  process.exitCode = 1;
}
