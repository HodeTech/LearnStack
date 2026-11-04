// Genuine copied initialized-router faults; native failure survives physical fixture cleanup.
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { existsSync, lstatSync, mkdtempSync, realpathSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { startBffProof } from './bff-proof-host.mjs';
import { assertBffPrivateOutput } from './bff-proof-support.mjs';
import { createFixtureOwner } from './fixture-support.mjs';
import { requestRouterFault, validateRouterFaultWire } from './framework-router-fault-client.mjs';
import { faultCases, finalCase } from './framework-router-fault-gate.mjs';
import { createRouterFaultHost } from './framework-router-fault-host.mjs';
import { createFaultFileReader } from './framework-router-fault-pipe-host.mjs';
import {
  describeRouterParser,
  deriveRouterPipeUnion,
  routerPipeInventory,
} from './framework-router-generated-pipes.mjs';
import { deriveRouterPipeSpec } from './framework-router-matrix-source.mjs';
import { deriveTransportSpec } from './framework-transport-proof-source.mjs';
import { stopTestChild } from './stop-test-child.mjs';
import { verifyFrameworkInstallation } from '../../../scripts/framework-installation.mjs';

const owner = createFixtureOwner();
owner.install();
const root = owner.ownRoot(realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-router-faults-'))));
const secret = randomBytes(32).toString('base64url'),
  marker = randomBytes(32).toString('base64url');
let running,
  proof,
  active,
  stage = 'start',
  stopResult = null,
  retired = false,
  disposed = false;
const wire = [];
let deadlineReached = false;
const deadline = setTimeout(() => {
  deadlineReached = true;
  active?.cancel();
  void owner.cancel();
}, 120_000);
deadline.unref();
const pause = () => new Promise((resolve) => setTimeout(resolve, 10));
function live() {
  assert.equal(deadlineReached, false);
  assert.equal(running.server.process.exitCode, null);
  assert.equal(running.server.process.signalCode, null);
}
async function waitFor(predicate) {
  while (!predicate()) {
    live();
    await pause();
  }
}
function released() {
  const records = running.server.broker.records();
  assert.equal(records.length, 3);
  assert.equal(new Set(records.map((row) => row.path)).size, 3);
  for (const row of records) {
    let absent = false;
    try {
      lstatSync(row.path);
    } catch (error) {
      if (error?.code === 'ENOENT') absent = true;
      else throw error;
    }
    assert.equal(absent, true);
  }
}
function persist(name, value) {
  writeFileSync(join(root, name), JSON.stringify(value) + '\n', { flag: 'wx', mode: 0o600 });
}
try {
  assert.equal(process.argv.length, 2, 'No router fault selectors');
  running = await startBffProof({
    owner,
    root,
    secret,
    marker,
    privateValues: [secret, marker],
    uiMode: true,
    isolatedProtocol: true,
    transportProof: true,
    routerFaults: true,
  });
  const identity = verifyFrameworkInstallation({ appRoot: running.app });
  const union = deriveRouterPipeUnion(
    Object.freeze({ identity, mode: 'production' }),
    deriveRouterPipeSpec(identity),
    describeRouterParser(identity),
  );
  proof = createRouterFaultHost({
    app: running.app,
    broker: running.server.broker,
    identity,
    union,
    subjects: { ...deriveTransportSpec(identity), parser: describeRouterParser(identity) },
  });
  const family = running.server.broker.capture.family();
  for (const name of faultCases) {
    stage = name + ':arm';
    proof.arm(name);
    stage = name + ':request';
    active = requestRouterFault({ port: proof.port, name });
    if (name === 'abort-before-selection') {
      await waitFor(() => {
        const rows = proof.inspect().cases.get(name) ?? [];
        return rows.some((row) => row.k === 'pull') && rows.some((row) => row.k === 'pipe');
      });
      active.abort();
    }
    const received = await active.completed;
    active = undefined;
    stage = name + ':observe';
    await waitFor(() => proof.completed(name));
    const record = proof.case(name),
      peer = validateRouterFaultWire(received, name);
    assert.equal(running.server.broker.capture.family(), family);
    wire.push({ case: record.case, wire: peer });
  }
  stage = 'constructor:stop';
  assert.equal(proof.completed(finalCase), true);
  stopResult = await stopTestChild(running.server.process);
  assert.deepEqual(stopResult, { forced: false, ownerCode: 1, ownerSignal: null });
  const close = await running.server.completed;
  assert.equal(close.code, 1);
  assert.equal(close.signal, null);
  assert.equal(running.server.broker.capture.retired(), true);
  retired = true;
  running.server.broker.assertHealthy();
  released();
  assertBffPrivateOutput(running.scanners);
  const observation = proof.closed();
  const nativeResult = JSON.parse(
    createFaultFileReader(join(root, '.router-runtime-result.json'), 2048, true)().toString('utf8'),
  );
  const nativeReceipt = {
    code: 1,
    signal: null,
    handshakeValidated: true,
    listenerReady: true,
    requestedStop: true,
    startupCancelled: false,
    parentLost: false,
    fault: 'channel-fault',
    directChildClosed: true,
    groupAbsent: true,
  };
  assert.deepEqual(nativeResult, {
    version: 1,
    ownerPid: running.server.process.pid,
    leaseNonce: running.server.broker.capture.lease().nonce,
    receipt: nativeReceipt,
  });
  const inventory = routerPipeInventory(union);
  const receipt = {
    version: 1,
    cases: wire,
    observations: observation.rows,
    manifestSha256: inventory.manifestSha256,
    inventorySha256: inventory.sha256,
    pipeEntries: observation.pipe.attestations.length,
    registrationNegatives: 3,
    expectedChildFailure: true,
    native: nativeReceipt,
    stop: stopResult,
    familyRetired: true,
    reservationsReleased: true,
    privateCleanup: false,
    constructionUnconfirmedAtExit: true,
  };
  persist('router-fault-receipt.json', receipt);
  stage = 'private-disposition';
  await owner.dispose();
  disposed = true;
  assert.equal(existsSync(root), false);
  receipt.privateCleanup = true;
  clearTimeout(deadline);
  assert.equal(deadlineReached, false);
  process.stdout.write(JSON.stringify(receipt) + '\n');
} catch {
  active?.cancel();
  if (active) await Promise.allSettled([active.completed]);
  try {
    await owner.dispose();
    disposed = true;
  } catch {
    /* Unknown or failed roots remain retained. */
  }
  clearTimeout(deadline);
  if (running) {
    try {
      retired = running.server.broker.capture.retired();
    } catch {
      retired = false;
    }
  }
  try {
    if (existsSync(root))
      persist('router-fault-failure.json', {
        version: 1,
        stage,
        stop: stopResult,
        familyRetired: retired,
        disposed,
        deadlineReached,
      });
  } catch {
    /* No new cleanup authority. */
  }
  process.stderr.write('Copied router fault proof failed.\n');
  process.exitCode = 1;
}
