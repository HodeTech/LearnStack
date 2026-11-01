import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

import {
  createBffShutdownValidator,
  createCopiedChildProof,
  NATIVE_OPERATION_KINDS,
} from './framework-proof-relay.mjs';

const incarnation = 'a'.repeat(32);
const wire = (payload, label = incarnation) => ({
  tag: 'learnstack-proof-v1',
  incarnation: label,
  payload,
});
function child(profile) {
  const sent = [];
  const stops = [];
  const receiver = createCopiedChildProof(profile).attach({
    send: (frame) => sent.push(frame),
    incarnation,
    requestStop: (reason) => stops.push(reason),
  });
  return { receiver, sent, stops };
}

test('none profile cannot pass a command or observation', () => {
  const { receiver } = child('none');
  receiver.onCommand(() => {});
  assert.throws(() => receiver.receive(wire({ kind: 'observe', id: 1, fence: 'b'.repeat(32) })));
  assert.throws(() => receiver.emit({ kind: 'readiness', pid: process.pid, phase: 'preparing' }));
});

test('ingress correlates observations and rejects malformed, wrong incarnation and replay', () => {
  const { receiver, sent } = child('ingress');
  const commands = [];
  receiver.onCommand((value) => commands.push(value));
  const observe = { kind: 'observe', id: 1, fence: 'b'.repeat(32) };
  assert.throws(() => receiver.receive(wire(observe, 'c'.repeat(32))));
  receiver.receive(wire(observe));
  assert.deepEqual(commands, [observe]);
  assert.throws(() => receiver.receive(wire(observe)));
  receiver.emit({ kind: 'readiness', pid: process.pid, phase: 'preparing' });
  receiver.emit({ kind: 'readiness', pid: process.pid, phase: 'prepared' });
  receiver.emit({ kind: 'bound', pid: process.pid, port: 3000, address: '127.0.0.1' });
  receiver.emit({ id: 1, nextCalls: 0, sinkCalls: 0, debugDisabled: true, environmentSafe: true });
  assert.throws(() =>
    receiver.emit({
      id: 1,
      nextCalls: 0,
      sinkCalls: 0,
      debugDisabled: true,
      environmentSafe: true,
    }),
  );
  assert.equal(sent.length, 4);
  assert.throws(() => receiver.emit({ surface: 'native', sanitized: true, secret: 'x' }));
});

test('admission accepts only fixed modes, safe counters and exact replies', () => {
  const { receiver } = child('admission');
  receiver.onCommand(() => {});
  assert.throws(() => receiver.receive(wire({ id: 1, op: 'mode', value: 'unbounded' })));
  receiver.receive(wire({ id: 1, op: 'mode', value: 'configuration' }));
  assert.throws(() =>
    receiver.emit({
      id: 1,
      active: 0,
      snapshots: 1,
      snapshotsSeen: 0,
      reads: 0,
      immutable: true,
      lateRefused: 0,
      lateAccepted: 0,
    }),
  );
  receiver.emit({
    id: 1,
    active: 1,
    snapshots: 1,
    snapshotsSeen: 1,
    reads: 2,
    immutable: true,
    lateRefused: 0,
    lateAccepted: 0,
  });
  assert.throws(() =>
    receiver.emit({
      id: 1,
      active: 0,
      snapshots: 0,
      snapshotsSeen: 1,
      reads: 2,
      immutable: true,
      lateRefused: 0,
      lateAccepted: 0,
    }),
  );
  receiver.receive(wire({ id: 2, op: 'settled' }));
  receiver.emit({
    id: 2,
    active: 0,
    snapshots: 0,
    snapshotsSeen: 1,
    reads: 2,
    immutable: true,
    lateRefused: 0,
    lateAccepted: 0,
  });
  receiver.emit({ kind: 'shutdown', counts: { active: 0, snapshots: 0 } });
  assert.throws(() => receiver.emit({ kind: 'shutdown', counts: { active: 0, snapshots: 0 } }));
});

test('BFF validates full native operation union and ordered shutdown frames', () => {
  const contract = readFileSync(
    new URL('../src/server/auth/native-operation-contract.ts', import.meta.url),
    'utf8',
  );
  const union = contract
    .split('export type NativeOperation =')[1]
    ?.split('export type NativeOperationResult =')[0];
  assert.ok(union, 'NativeOperation union available');
  const kinds = [...union.matchAll(/'([A-Za-z][A-Za-z0-9]+)'/g)].map((match) => match[1]);
  assert.deepEqual([...NATIVE_OPERATION_KINDS].sort(), kinds.sort());
  const { receiver } = child('bff');
  receiver.onCommand(() => {});
  receiver.receive(wire({ kind: 'advance', milliseconds: 60000 }));
  receiver.emit({
    kind: 'snapshot',
    now: 60000,
    refreshes: 0,
    accounts: 0,
    administrations: { listOrganizations: 1, selectDefaultOrganization: 1 },
  });
  receiver.receive(wire({ kind: 'render-control', action: 'status' }));
  receiver.emit({ kind: 'render-state', held: 0, frames: 0, snapshots: null });
  receiver.emit({ kind: 'proof-listener', port: 12345 });
  assert.throws(() => receiver.emit({ kind: 'proof-listener', port: 12345 }));
  const stream = createBffShutdownValidator();
  const first = {
    kind: 'proof-shutdown',
    entered: true,
    watchdog: false,
    listener: 'pending',
    next: 'pending',
    sessions: 'pending',
    requests: 'pending',
    milestone: 'entered',
    sample: [0, 0, 0, 0, 0],
  };
  stream.observe(first);
  stream.observe({ ...first, listener: 'fulfilled', milestone: 'listener' });
  assert.throws(() => stream.observe({ ...first, listener: 'pending', milestone: 'listener' }));
  receiver.emit(first);
  receiver.emit({ ...first, listener: 'fulfilled', milestone: 'listener' });
});

test('drain joins a held real command before its correlated reply and seals later commands', async () => {
  const { receiver, sent } = child('admission');
  let release;
  const held = new Promise((resolve) => {
    release = resolve;
  });
  receiver.onCommand(async (value) => {
    await held;
    receiver.emit({
      id: value.id,
      active: 0,
      snapshots: 0,
      snapshotsSeen: 0,
      reads: 0,
      immutable: true,
      lateRefused: 0,
      lateAccepted: 0,
    });
  });
  assert.equal(receiver.receive(wire({ id: 1, op: 'settled' })), undefined);
  const drain = receiver.drain();
  assert.equal(receiver.drain(), drain);
  assert.equal(sent.length, 0);
  assert.throws(() => receiver.receive(wire({ id: 2, op: 'observe' })));
  release();
  await drain;
  assert.equal(sent.length, 1);
  assert.equal(sent[0].payload.id, 1);
});

test('rejected asynchronous command preserves protocol failure through drain', async () => {
  const { receiver, stops } = child('bff');
  receiver.onCommand(async () => {
    throw new Error('held proof command failed');
  });
  receiver.receive(wire({ kind: 'advance', milliseconds: 0 }));
  await assert.rejects(receiver.drain(), /protocol refused/);
  assert.deepEqual(stops, ['protocol-fault']);
});

test(
  'copied owner joins queued host sends before intentional IPC disconnect',
  { timeout: 5000 },
  async () => {
    const script = `
    import { createCopiedParentProof } from ${JSON.stringify(new URL('./framework-proof-relay.mjs', import.meta.url).href)};
    const originalSend = process.send.bind(process);
    let acknowledge;
    process.send = (_value, callback) => {
      acknowledge = callback;
      originalSend({ kind: 'queued' });
      return true;
    };
    const proof = createCopiedParentProof('none');
    proof.onLease({ record: { format: 1, pid: process.pid, nonce: 'a'.repeat(32), kind: 'production' }, records: [{}, {}, {}] });
    process.on('message', (message) => { if (message.kind === 'release') acknowledge(null); });
    const closing = proof.close();
    setImmediate(() => originalSend({ kind: 'waiting' }));
    await closing;
  `;
    const owner = spawn(process.execPath, ['--input-type=module', '-e', script], {
      stdio: ['ignore', 'pipe', 'pipe', 'ipc'],
    });
    const messages = [];
    const waiting = new Promise((resolve) => {
      owner.on('message', (message) => {
        messages.push(message.kind);
        if (message.kind === 'waiting') resolve();
      });
    });
    const closed = new Promise((resolve, reject) => {
      owner.once('error', reject);
      owner.once('close', (code, signal) => resolve({ code, signal }));
    });
    try {
      await waiting;
      assert.deepEqual(messages, ['queued', 'waiting']);
      assert.equal(owner.connected, true);
      owner.send({ kind: 'release' });
      assert.deepEqual(await closed, { code: 0, signal: null });
    } finally {
      if (owner.exitCode === null && owner.signalCode === null) owner.kill('SIGKILL');
    }
  },
);

test('unexpected host IPC loss signals the copied owner live PID', { timeout: 5000 }, async () => {
  const script = `
    import { createCopiedParentProof } from ${JSON.stringify(new URL('./framework-proof-relay.mjs', import.meta.url).href)};
    const keepalive = setInterval(() => {}, 1000);
    let proof;
    const terminate = async () => {
      clearInterval(keepalive);
      process.exitCode = 17;
      try { await proof.close(); } catch {}
      process.off('SIGTERM', terminate);
      process.exit(17);
    };
    process.on('SIGTERM', terminate);
    proof = createCopiedParentProof('none');
    process.send({ kind: 'armed' });
  `;
  const owner = spawn(process.execPath, ['--input-type=module', '-e', script], {
    stdio: ['ignore', 'ignore', 'ignore', 'ipc'],
  });
  const armed = new Promise((resolve, reject) => {
    owner.once('message', resolve);
    owner.once('close', (code, signal) =>
      reject(new Error(`Owner closed before arm: ${code}/${signal}`)),
    );
  });
  const exited = new Promise((resolve, reject) => {
    owner.once('error', reject);
    owner.once('exit', (code, signal) => resolve({ code, signal }));
  });
  try {
    assert.deepEqual(await armed, { kind: 'armed' });
    owner.disconnect();
    assert.deepEqual(await exited, { code: 17, signal: null });
  } finally {
    if (owner.exitCode === null && owner.signalCode === null) owner.kill('SIGKILL');
  }
});
