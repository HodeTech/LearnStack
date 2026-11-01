// Private bookkeeping controls; fake handles/promises do not demonstrate a Next run.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync, realpathSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createCopiedOverlapProof } from '../apps/web/scripts/framework-proof-overlap.mjs';
import {
  assertCompletedOverlap,
  validateCopiedOverlapRows,
} from '../apps/web/scripts/framework-overlap-proof-rows.mjs';

function gate() {
  let release;
  const promise = new Promise((resolve) => {
    release = resolve;
  });
  return { promise, release };
}
const flush = async () => {
  for (let i = 0; i < 12; i++) await Promise.resolve();
};

async function controlledCase(handlerBeforeStop) {
  const root = mkdtempSync(join(realpathSync(tmpdir()), 'overlap-control-'));
  const app = join(root, 'frontend/apps/web');
  mkdirSync(app, { recursive: true });
  const proof = createCopiedOverlapProof({ identity: { appRoot: app }, mode: 'production' });
  const frameA = {},
    frameB = {},
    frameAClean = gate(),
    frameBClean = gate(),
    leaseAClean = gate(),
    leaseBClean = gate();
  const trackedA = [],
    trackedB = [];
  const leaseA = {
    cleaned: leaseAClean.promise,
    track(actual) {
      trackedA.push(actual);
      proof.track(leaseA, actual);
    },
  };
  const leaseB = {
    cleaned: leaseBClean.promise,
    track(actual) {
      trackedB.push(actual);
      proof.track(leaseB, actual);
    },
  };
  let render = gate(),
    stop = gate(),
    registeredFramework;
  try {
    proof.arm();
    proof.reserve(frameB, leaseB, frameBClean.promise);
    const hold = proof.claimApi();
    assert.ok(hold);
    const actualApi = hold.then(() => true);
    proof.observeApi(actualApi);
    const actualBounded = actualApi.then(() => true);
    leaseB.track(actualBounded);
    proof.bounded(leaseB, actualBounded);
    proof.reserve(frameA, leaseA, frameAClean.promise);
    proof.registerRender(render.promise, {
      waitUntil(actual) {
        registeredFramework = actual;
      },
    });
    assert.notEqual(registeredFramework, render.promise);
    proof.responseClose();
    proof.encoderClose();
    if (handlerBeforeStop) proof.handler(frameA, 'fulfilled');
    proof.startNativeStop({
      stop() {
        return stop.promise;
      },
    });
    assert.equal(proof.snapshot().stageAcknowledged, true);
    proof.releaseNext(() => {
      render.release();
    });
    assert.equal(proof.snapshot().stageAcknowledged, false);
    assert.throws(() => proof.releaseNext(() => {}));
    await flush();
    assert.equal(proof.snapshot().stageAcknowledged, true);
    proof.releaseNext(() => {});
    await registeredFramework;
    await flush();
    assert.equal(proof.snapshot().stageAcknowledged, false);
    frameAClean.release();
    await flush();
    assert.equal(proof.snapshot().stageAcknowledged, true);
    proof.releaseNext(() => {});
    await Promise.all(trackedA);
    await flush();
    assert.equal(proof.snapshot().stageAcknowledged, false);
    leaseAClean.release();
    await flush();
    assert.equal(proof.snapshot().stageAcknowledged, true);
    proof.releaseNext(() => {});
    await Promise.all(trackedB);
    if (!handlerBeforeStop) proof.handler(frameA, 'rejected');
    proof.handler(frameB, 'rejected');
    frameBClean.release();
    leaseBClean.release();
    await flush();
    stop.release();
    await flush();
    await proof.close();
    const rows = validateCopiedOverlapRows(
      readFileSync(join(root, '.overlap-proof.events'), 'utf8'),
      process.pid,
    );
    assert.equal(assertCompletedOverlap(rows).nativeStop, 'fulfilled');
    const before = rows.find((row) => row.event === 'snapshot' && row.state.releaseIndex === 0);
    assert.equal(before.state.aHandler, handlerBeforeStop ? 'fulfilled' : 'unknown');
    const mutated = rows.map((row) => ({ ...row }));
    mutated.push({
      version: 1,
      pid: process.pid,
      sequence: mutated.length + 1,
      event: 'publish-b',
      state: null,
    });
    assert.throws(() => assertCompletedOverlap(mutated));
    assert.throws(() =>
      validateCopiedOverlapRows(
        JSON.stringify({ ...rows[0], event: 'unknown' }) + '\n',
        process.pid,
      ),
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test('settled real-handler observation does not block independent owners (model)', async () =>
  controlledCase(true));
test('pending real-handler observation remains measured, not fabricated (model)', async () =>
  controlledCase(false));
