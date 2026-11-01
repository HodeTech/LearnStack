import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { test } from 'node:test';

import { stopProductionRuntimeOwner } from './stop-test-child.mjs';

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
