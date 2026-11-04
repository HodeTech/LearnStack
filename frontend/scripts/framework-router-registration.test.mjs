import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import test from 'node:test';
import zlib from 'node:zlib';

import { createResponseCompressionOwner } from '../framework/next-response-cleanup/owner.mjs';
import {
  registrationCases,
  requireRegisteredOwner,
} from '../apps/web/scripts/framework-router-fault-gate.mjs';

class NativeResponseControl extends EventEmitter {
  destroyed = false;
  destroy() {
    if (this.destroyed) return;
    this.destroyed = true;
    this.emit('close');
  }
}

test('three registration negatives have distinct fixed names', () => {
  assert.deepEqual(registrationCases, [
    'missing-required-owner',
    'duplicate-owner',
    'late-drain-registration',
  ]);
});

test('required-owner omission is refused, while an optional no-middleware route is not relabeled', async () => {
  const response = new NativeResponseControl();
  assert.throws(() => requireRegisteredOwner(response), /Required owner registration missing/);
  const owner = createResponseCompressionOwner(response);
  assert.equal(requireRegisteredOwner(response), owner);
  owner.markNoCompression();
  await owner.join();
});

test('duplicate owner installation refuses before changing slot, listeners or allocation', async () => {
  const response = new NativeResponseControl();
  const original = createResponseCompressionOwner(response);
  const before = Object.freeze({
    finish: response.listenerCount('finish'),
    close: response.listenerCount('close'),
    drain: response.listenerCount('drain'),
  });
  assert.throws(
    () => createResponseCompressionOwner(response),
    /Duplicate response compression owner/,
  );
  assert.equal(requireRegisteredOwner(response), original);
  assert.deepEqual(
    {
      finish: response.listenerCount('finish'),
      close: response.listenerCount('close'),
      drain: response.listenerCount('drain'),
    },
    before,
  );
  assert.equal(original.failureState().constructionUnconfirmed, false);
  original.markNoCompression();
  await original.join();
});

test('late drain registration refuses after real encoder close without retaining a callback', async () => {
  const response = new NativeResponseControl();
  const owner = createResponseCompressionOwner(response);
  const stream = owner.createCompressor(() => zlib.createGzip());
  const closed = new Promise((resolve) => stream.once('close', resolve));
  stream.destroy();
  await closed;
  const before = response.listenerCount('drain');
  assert.throws(
    () => owner.setResponseDrainListener(() => {}),
    /Invalid response compression drain registration/,
  );
  assert.equal(response.listenerCount('drain'), before);
  await owner.join();
});
