import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

import {
  createBffShutdownValidator,
  createCopiedChildProof,
  createCopiedHostProofValidator,
  NATIVE_OPERATION_KINDS,
} from './framework-proof-relay.mjs';
import { boundNextVersionFetch } from './ingress-readiness.mjs';

const incarnation = 'a'.repeat(32);
const wire = (payload, label = incarnation) => ({
  tag: 'learnstack-proof-v1',
  incarnation: label,
  payload,
});
const bffCommand = (sequence, payload) => ({ kind: 'proof-command', sequence, payload });
const bffReply = (sequence, payload) => ({ kind: 'proof-reply', sequence, payload });
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
  assert.throws(() => receiver.receive(wire(observe)));
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
  assert.throws(() => receiver.receive(wire({ id: 1, op: 'mode', value: 'configuration' })));
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

test('completed IDs cannot replay, while distinct out-of-order IDs remain valid', () => {
  const ingress = createCopiedHostProofValidator('ingress');
  const ingressReply = (id) => ({
    id,
    nextCalls: 0,
    sinkCalls: 0,
    debugDisabled: true,
    environmentSafe: true,
  });
  ingress.command({ kind: 'observe', id: 9, fence: 'b'.repeat(32) });
  ingress.observation(ingressReply(9), 123);
  assert.throws(() => ingress.command({ kind: 'observe', id: 9, fence: 'b'.repeat(32) }));
  ingress.command({ kind: 'observe', id: 2, fence: 'c'.repeat(32) });
  ingress.observation(ingressReply(2), 123);

  const admission = createCopiedHostProofValidator('admission');
  const reply = (id) => ({
    id,
    active: 0,
    snapshots: 0,
    snapshotsSeen: 0,
    reads: 0,
    immutable: true,
    lateRefused: 0,
    lateAccepted: 0,
  });
  admission.command({ id: 8, op: 'observe' });
  admission.observation(reply(8), 123);
  assert.throws(() => admission.command({ id: 8, op: 'observe' }));
  admission.command({ id: 3, op: 'settled' });
  admission.observation(reply(3), 123);
});

test('ingress readiness and concurrent version fetches preserve causal transitions', () => {
  const ingress = createCopiedHostProofValidator('ingress');
  const pid = 123;
  const fetch = (sequence, state, errorKind, elapsedMilliseconds) => ({
    kind: 'version-fetch',
    pid,
    sequence,
    state,
    errorKind,
    elapsedMilliseconds,
  });
  assert.throws(() => ingress.observation({ kind: 'readiness', pid, phase: 'prepared' }, pid));
  ingress.observation({ kind: 'readiness', pid, phase: 'preparing' }, pid);
  ingress.observation({ kind: 'readiness', pid, phase: 'prepared' }, pid);
  assert.throws(() => ingress.observation({ kind: 'readiness', pid, phase: 'prepared' }, pid));
  assert.throws(() => ingress.observation(fetch(1, 'failed', 'connection_refused', 1), pid));
  ingress.observation(fetch(1, 'pending', 'none', 4), pid);
  ingress.observation(fetch(2, 'pending', 'none', 7), pid);
  assert.throws(() => ingress.observation(fetch(1, 'failed', 'none', 8), pid));
  assert.throws(() => ingress.observation(fetch(1, 'settled', 'timeout', 8), pid));
  ingress.observation(fetch(1, 'failed', 'connection_refused', 9), pid);
  assert.throws(() => ingress.observation(fetch(1, 'failed', 'connection_refused', 9), pid));
  ingress.observation(fetch(2, 'settled', 'none', 10), pid);
  assert.throws(() => ingress.observation(fetch(2, 'settled', 'none', 11), pid));
});

test('swallowed invalid version callback poisons proof, while concurrent real callbacks settle', async () => {
  const bad = child('ingress');
  const badFetch = boundNextVersionFetch(
    () => Promise.resolve({ ok: true }),
    (observation, sequence) => {
      bad.receiver.emit({
        kind: 'version-fetch',
        pid: process.pid,
        sequence,
        ...observation,
        ...(observation.state === 'pending' ? { errorKind: 'timeout' } : {}),
      });
    },
  );
  assert.deepEqual(await badFetch('https://registry.npmjs.org/-/package/next/dist-tags'), {
    ok: true,
  });
  assert.ok(bad.stops.length > 0);
  assert.ok(bad.stops.every((reason) => reason === 'protocol-fault'));
  await assert.rejects(bad.receiver.drain(), /protocol refused/);

  const valid = child('ingress');
  const releases = [];
  const validFetch = boundNextVersionFetch(
    () => new Promise((resolve) => releases.push(resolve)),
    (observation, sequence) =>
      valid.receiver.emit({ kind: 'version-fetch', pid: process.pid, sequence, ...observation }),
  );
  const first = validFetch('https://registry.npmjs.org/-/package/next/dist-tags');
  const second = validFetch('https://registry.npmjs.org/-/package/next/dist-tags');
  releases[1]({ value: 1 });
  releases[0]({ value: 1 });
  assert.deepEqual(await Promise.all([first, second]), [{ value: 1 }, { value: 1 }]);
  assert.deepEqual(
    valid.sent.map((frame) => frame.payload.state),
    ['pending', 'pending', 'settled', 'settled'],
  );
  assert.deepEqual(
    valid.sent.map((frame) => frame.payload.sequence),
    [1, 2, 2, 1],
  );
  assert.deepEqual(valid.stops, []);
  await valid.receiver.drain();
});

test('real fetch wrapper refuses replayed A terminal while B remains pending', async () => {
  const proof = child('ingress');
  const releases = [];
  const fetch = boundNextVersionFetch(
    () => new Promise((resolve) => releases.push(resolve)),
    (observation, sequence) => {
      const frame = { kind: 'version-fetch', pid: process.pid, sequence, ...observation };
      proof.receiver.emit(frame);
      if (observation.state === 'settled' && sequence === 1) proof.receiver.emit(frame);
    },
  );
  const first = fetch('https://registry.npmjs.org/-/package/next/dist-tags');
  const second = fetch('https://registry.npmjs.org/-/package/next/dist-tags');
  releases[0]({ value: 'same' });
  assert.deepEqual(await first, { value: 'same' });
  assert.deepEqual(
    proof.sent.map((frame) => frame.payload.sequence),
    [1, 2, 1],
  );
  assert.deepEqual(proof.stops, ['protocol-fault']);
  releases[1]({ value: 'same' });
  assert.deepEqual(await second, { value: 'same' });
  assert.deepEqual(
    proof.sent.map((frame) => frame.payload.sequence),
    [1, 2, 1, 2],
  );
  await assert.rejects(proof.receiver.drain(), /protocol refused/);
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
  receiver.onCommand((command) => {
    if (command.kind === 'advance')
      receiver.emit({
        kind: 'snapshot',
        now: command.milliseconds,
        refreshes: 0,
        accounts: 0,
        administrations: { listOrganizations: 1, selectDefaultOrganization: 1 },
      });
    else receiver.emit({ kind: 'render-state', held: 0, frames: 0, snapshots: null });
  });
  receiver.receive(wire(bffCommand(1, { kind: 'advance', milliseconds: 60000 })));
  receiver.receive(wire(bffCommand(2, { kind: 'render-control', action: 'status' })));
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

test('BFF sequence and cumulative clock refuse stale replies after valid sequential traffic', () => {
  const host = createCopiedHostProofValidator('bff');
  const snapshot = (now) => ({
    kind: 'snapshot',
    now,
    refreshes: 0,
    accounts: 0,
    administrations: {},
  });
  host.command(bffCommand(1, { kind: 'advance', milliseconds: 0 }));
  host.observation(bffReply(1, snapshot(0)), 123);
  host.command(bffCommand(2, { kind: 'advance', milliseconds: 60000 }));
  assert.throws(() => host.observation(bffReply(1, snapshot(0)), 123));
  assert.throws(() => host.observation(bffReply(2, snapshot(0)), 123));
  host.observation(bffReply(2, snapshot(60000)), 123);
  host.command(bffCommand(3, { kind: 'render-control', action: 'status' }));
  assert.throws(() =>
    host.observation(
      bffReply(2, { kind: 'render-state', held: 0, frames: 0, snapshots: null }),
      123,
    ),
  );
  host.observation(bffReply(3, { kind: 'render-state', held: 0, frames: 0, snapshots: null }), 123);
  assert.throws(() => host.command(bffCommand(3, { kind: 'advance', milliseconds: 0 })));
});

test('child async context cannot retag an old BFF reply for a later command', async () => {
  const { receiver, sent } = child('bff');
  let releaseOld;
  const old = new Promise((resolve) => {
    releaseOld = resolve;
  });
  let oldAttempt;
  receiver.onCommand((command) => {
    if (command.milliseconds === 0) {
      oldAttempt = old.then(() => {
        assert.throws(() =>
          receiver.emit({
            kind: 'snapshot',
            now: 0,
            refreshes: 0,
            accounts: 0,
            administrations: {},
          }),
        );
      });
      receiver.emit({ kind: 'snapshot', now: 0, refreshes: 0, accounts: 0, administrations: {} });
    }
  });
  receiver.receive(wire(bffCommand(1, { kind: 'advance', milliseconds: 0 })));
  receiver.receive(wire(bffCommand(2, { kind: 'advance', milliseconds: 60000 })));
  releaseOld();
  await oldAttempt;
  assert.equal(sent.length, 1);
  assert.equal(sent[0].payload.sequence, 1);
});

test('BFF copied watchdog registration is single and emits through the closed shutdown validator', () => {
  const { receiver, sent } = child('bff');
  const entered = {
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
  receiver.emit(entered);
  receiver.onWatchdog(() => receiver.emit({ ...entered, watchdog: true, milestone: 'watchdog' }));
  assert.throws(() => receiver.onWatchdog(() => {}));
  assert.equal(receiver.watchdog(), undefined);
  assert.equal(receiver.watchdog(), undefined);
  assert.equal(sent.length, 2);
  assert.equal(sent[1].payload.milestone, 'watchdog');
  const other = child('ingress');
  assert.throws(() => other.receiver.onWatchdog(() => {}));
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
  receiver.receive(wire(bffCommand(1, { kind: 'advance', milliseconds: 0 })));
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

test(
  'copied parent forwards correlated BFF wires and refuses a stale child reply',
  { timeout: 5000 },
  async () => {
    const script = `
    import { createCopiedParentProof } from ${JSON.stringify(new URL('./framework-proof-relay.mjs', import.meta.url).href)};
    const incarnation = 'b'.repeat(32);
    const childPid = 34567;
    const proof = createCopiedParentProof('bff');
    proof.onLease({ record: { format: 1, kind: 'production', pid: process.pid, nonce: 'a'.repeat(32) }, records: [{}, {}, {}] });
    let receiver;
    receiver = proof.attach({
      childPid, incarnation,
      onFault: () => process.send({ kind: 'unexpected-fault' }),
      send(frame) {
        const snapshot = { kind: 'snapshot', now: 0, refreshes: 0, accounts: 0, administrations: {} };
        const reply = { tag: 'learnstack-proof-v1', incarnation,
          payload: { kind: 'proof-reply', sequence: 1, payload: snapshot } };
        if (frame.payload.sequence === 1) receiver.receive(reply);
        else {
          let refused = false;
          try { receiver.receive(reply); } catch { refused = true; }
          process.send({ kind: 'stale-refused', refused }, () => { void proof.close(); });
        }
      },
    });
    proof.onRuntimeFamily({ pid: childPid, group: childPid, parentPid: process.pid,
      leaseNonce: 'a'.repeat(32), incarnation });
  `;
    const owner = spawn(process.execPath, ['--input-type=module', '-e', script], {
      stdio: ['ignore', 'ignore', 'ignore', 'ipc'],
    });
    const messages = [];
    owner.on('message', (message) => {
      messages.push(message);
      if (message.kind === 'runtime-family')
        owner.send(bffCommand(1, { kind: 'advance', milliseconds: 0 }));
      if (message.kind === 'proof-reply')
        owner.send(bffCommand(2, { kind: 'advance', milliseconds: 60000 }));
    });
    const closed = new Promise((resolve, reject) => {
      owner.once('error', reject);
      owner.once('close', (code, signal) => resolve({ code, signal }));
    });
    try {
      assert.deepEqual(await closed, { code: 0, signal: null });
      assert.deepEqual(
        messages.map((value) => value.kind),
        ['runtime-lease', 'runtime-family', 'proof-reply', 'stale-refused'],
      );
      assert.equal(messages[2].sequence, 1);
      assert.equal(messages[3].refused, true);
    } finally {
      if (owner.exitCode === null && owner.signalCode === null) owner.kill('SIGKILL');
    }
  },
);

test(
  'copied parent mints distinct producer ordinals for reused PID and refuses handle replay',
  { timeout: 5000 },
  async () => {
    const script = `
    import { createCopiedParentProof } from ${JSON.stringify(new URL('./framework-proof-relay.mjs', import.meta.url).href)};
    const proof = createCopiedParentProof('none');
    proof.onLease({ record: { format: 1, kind: 'development', pid: process.pid, nonce: 'a'.repeat(32) }, records: [{}, {}, {}] });
    const first = Object.freeze({ pid: 34567, group: 34567, parentPid: process.pid });
    const second = Object.freeze({ pid: 34567, group: 34567, parentPid: process.pid });
    proof.onProducerFamily(first);
    proof.onProducerFamily(second);
    let refused = false;
    try { proof.onProducerFamily(first); } catch { refused = true; }
    process.send({ kind: 'replay-refused', refused });
    await proof.close();
  `;
    const owner = spawn(process.execPath, ['--input-type=module', '-e', script], {
      stdio: ['ignore', 'ignore', 'ignore', 'ipc'],
    });
    const messages = [];
    owner.on('message', (message) => messages.push(message));
    const closed = new Promise((resolve, reject) => {
      owner.once('error', reject);
      owner.once('close', (code, signal) => resolve({ code, signal }));
    });
    try {
      assert.deepEqual(await closed, { code: 0, signal: null });
      assert.deepEqual(
        messages.map((message) => message.kind),
        ['runtime-lease', 'producer-family', 'producer-family', 'replay-refused'],
      );
      assert.deepEqual(
        messages.slice(1, 3).map((message) => message.ordinal),
        [1, 2],
      );
      assert.deepEqual(
        messages.slice(1, 3).map((message) => message.pid),
        [34567, 34567],
      );
      assert.equal(messages[3].refused, true);
    } finally {
      if (owner.exitCode === null && owner.signalCode === null) owner.kill('SIGKILL');
    }
  },
);

test(
  'copied parent refuses duplicate fetch A terminal while B remains pending',
  { timeout: 5000 },
  async () => {
    const script = `
    import { createCopiedParentProof } from ${JSON.stringify(new URL('./framework-proof-relay.mjs', import.meta.url).href)};
    const proof = createCopiedParentProof('ingress');
    const incarnation = 'b'.repeat(32);
    const childPid = 34567;
    proof.onLease({ record: { format: 1, kind: 'production', pid: process.pid, nonce: 'a'.repeat(32) }, records: [{}, {}, {}] });
    const receiver = proof.attach({ childPid, incarnation, send() {}, onFault() { throw Error('fault'); } });
    proof.onRuntimeFamily({ pid: childPid, group: childPid, parentPid: process.pid,
      leaseNonce: 'a'.repeat(32), incarnation });
    const observe = (sequence, state) => ({ tag: 'learnstack-proof-v1', incarnation,
      payload: { kind: 'version-fetch', pid: childPid, sequence, state,
        errorKind: 'none', elapsedMilliseconds: 1 } });
    receiver.receive(observe(1, 'pending'));
    receiver.receive(observe(2, 'pending'));
    receiver.receive(observe(1, 'settled'));
    let refused = false;
    try { receiver.receive(observe(1, 'settled')); } catch { refused = true; }
    process.send({ kind: 'replay-refused', refused });
    await proof.close();
  `;
    const owner = spawn(process.execPath, ['--input-type=module', '-e', script], {
      stdio: ['ignore', 'ignore', 'ignore', 'ipc'],
    });
    const messages = [];
    owner.on('message', (message) => messages.push(message));
    const closed = new Promise((resolve, reject) => {
      owner.once('error', reject);
      owner.once('close', (code, signal) => resolve({ code, signal }));
    });
    try {
      assert.deepEqual(await closed, { code: 0, signal: null });
      assert.deepEqual(
        messages.map((message) => message.kind),
        [
          'runtime-lease',
          'runtime-family',
          'version-fetch',
          'version-fetch',
          'version-fetch',
          'replay-refused',
        ],
      );
      assert.deepEqual(
        messages.slice(2, 5).map((message) => message.sequence),
        [1, 2, 1],
      );
      assert.equal(messages[5].refused, true);
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
