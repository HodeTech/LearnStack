import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { test } from 'node:test';

import {
  markRuntimeTestChild,
  stopProductionRuntimeOwner,
  stopTestChild,
} from './stop-test-child.mjs';

function owner({ code = 0, signal = null, alreadyExited = false, delivery = true } = {}) {
  const child = new EventEmitter();
  child.pid = 23456;
  child.exitCode = alreadyExited ? code : null;
  child.signalCode = alreadyExited ? signal : null;
  child.killed = false;
  child.signals = [];
  child.kill = (name) => {
    child.signals.push(name);
    if (!delivery) return false;
    child.killed = true;
    queueMicrotask(() => {
      child.exitCode = code;
      child.signalCode = signal;
      child.emit('close', code, signal);
    });
    return true;
  };
  return child;
}

test('production driver signals the live owner PID once and joins clean close', async () => {
  const child = owner();
  const first = stopProductionRuntimeOwner(child);
  assert.equal(stopProductionRuntimeOwner(child), first);
  assert.deepEqual(await first, { forced: false });
  assert.deepEqual(child.signals, ['SIGTERM']);
});

test('already exited owner cannot gain retirement credit', async () => {
  const child = owner({ alreadyExited: true });
  await assert.rejects(stopProductionRuntimeOwner(child), /already lost/);
  assert.deepEqual(child.signals, []);
});

test('nonzero and signal exits preserve shutdown failure', async () => {
  for (const child of [owner({ code: 1 }), owner({ code: null, signal: 'SIGKILL' })]) {
    await assert.rejects(stopProductionRuntimeOwner(child), /retirement unconfirmed/);
    assert.deepEqual(child.signals, ['SIGTERM']);
  }
});

test('undeliverable owner stop cannot be treated as a clean close', async () => {
  const child = owner({ delivery: false });
  await assert.rejects(stopProductionRuntimeOwner(child), /process failed/);
  assert.deepEqual(child.signals, ['SIGTERM']);
});

test('reused producer PID joins each distinct group once without a saved group signal', async () => {
  const child = new EventEmitter();
  child.pid = 23456;
  child.exitCode = null;
  child.signalCode = null;
  child.kill = (signal) => {
    assert.equal(signal, 'SIGTERM');
    queueMicrotask(() => {
      child.exitCode = 0;
      child.emit('close', 0, null);
    });
    return true;
  };
  const capture = markRuntimeTestChild(child);
  capture.captureLease({ format: 1, kind: 'development', nonce: 'a'.repeat(32), pid: child.pid });
  capture.captureProducerFamily({
    ordinal: 1,
    pid: 34567,
    group: 34567,
    parentPid: child.pid,
    leaseNonce: 'a'.repeat(32),
  });
  capture.captureFamily({
    pid: 34567,
    group: 34567,
    parentPid: child.pid,
    leaseNonce: 'a'.repeat(32),
    incarnation: 'b'.repeat(32),
  });
  const originalKill = process.kill;
  const probes = [];
  process.kill = (pid, signal) => {
    assert.equal(signal, 0);
    probes.push(pid);
    const error = new Error('group absent');
    error.code = 'ESRCH';
    throw error;
  };
  try {
    assert.deepEqual(await stopTestChild(child), {
      forced: false,
      ownerCode: 0,
      ownerSignal: null,
    });
    assert.deepEqual(probes, [-23456, -34567]);
    assert.equal(capture.retired(), true);
  } finally {
    process.kill = originalKill;
  }
});

test('sequential producer generations may reuse a PID and both remain captured for retirement', async () => {
  const child = new EventEmitter();
  child.pid = 23456;
  child.exitCode = null;
  child.signalCode = null;
  child.kill = (signal) => {
    assert.equal(signal, 'SIGTERM');
    queueMicrotask(() => {
      child.exitCode = 0;
      child.emit('close', 0, null);
    });
    return true;
  };
  const capture = markRuntimeTestChild(child);
  const nonce = 'a'.repeat(32);
  capture.captureLease({ format: 1, kind: 'development', nonce, pid: child.pid });
  const producer = { pid: 34567, group: 34567, parentPid: child.pid, leaseNonce: nonce };
  capture.captureProducerFamily({ ...producer, ordinal: 1 });
  capture.captureProducerFamily({ ...producer, ordinal: 2 });
  assert.deepEqual(
    capture.producers().map((handle) => handle.ordinal),
    [1, 2],
  );
  assert.throws(() => capture.captureProducerFamily({ ...producer, ordinal: 2 }));
  capture.captureFamily({
    pid: 34567,
    group: 34567,
    parentPid: child.pid,
    leaseNonce: nonce,
    incarnation: 'b'.repeat(32),
  });
  const originalKill = process.kill;
  const probes = [];
  process.kill = (pid, signal) => {
    assert.equal(signal, 0);
    probes.push(pid);
    const error = new Error('group absent');
    error.code = 'ESRCH';
    throw error;
  };
  try {
    assert.deepEqual(await stopTestChild(child), {
      forced: false,
      ownerCode: 0,
      ownerSignal: null,
    });
    assert.deepEqual(probes, [-23456, -34567]);
    assert.equal(capture.retired(), true);
  } finally {
    process.kill = originalKill;
  }
});
