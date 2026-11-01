import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { EventEmitter } from 'node:events';
import {
  chmodSync,
  copyFileSync,
  mkdirSync,
  mkdtempSync,
  realpathSync,
  rmSync,
  symlinkSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { test } from 'node:test';
import { pathToFileURL } from 'node:url';

const sourceApp = realpathSync(new URL('../apps/web/', import.meta.url));

async function fixture(kind = 'production') {
  const root = realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-proof-broker-')));
  const guardFrontend = join(root, 'guard-frontend');
  const copyFrontend = join(root, 'fixture-frontend');
  const app = join(copyFrontend, 'apps/web');
  const guardLink = join(copyFrontend, 'scripts');
  mkdirSync(join(guardFrontend, 'scripts'), { recursive: true });
  mkdirSync(join(app, 'scripts'), { recursive: true });
  for (const name of ['framework-installation.mjs', 'framework-process-family.mjs'])
    copyFileSync(resolve(sourceApp, '../../scripts', name), join(guardFrontend, 'scripts', name));
  for (const name of [
    'framework-proof-host-broker.mjs',
    'framework-proof-relay.mjs',
    'stop-test-child.mjs',
  ])
    copyFileSync(join(sourceApp, 'scripts', name), join(app, 'scripts', name));
  symlinkSync(join(guardFrontend, 'scripts'), guardLink, 'dir');
  const { createCopiedRuntimeHostBroker } = await import(
    pathToFileURL(join(app, 'scripts/framework-proof-host-broker.mjs')).href
  );
  const { stopTestChild } = await import(
    pathToFileURL(join(app, 'scripts/stop-test-child.mjs')).href
  );
  const nonce = randomBytes(16).toString('hex');
  const record = { format: 1, pid: 23456, nonce, kind };
  for (const rootPath of [guardFrontend, app, join(copyFrontend, 'packages/sdk')]) {
    const state = join(rootPath, '.framework-state');
    const readers = join(state, 'readers');
    mkdirSync(readers, { recursive: true });
    chmodSync(state, 0o700);
    chmodSync(readers, 0o700);
  }
  for (const path of [
    join(guardFrontend, '.framework-state/readers', nonce),
    join(app, '.framework-state/writer.json'),
    kind === 'production'
      ? join(copyFrontend, 'packages/sdk/.framework-state/readers', nonce)
      : join(copyFrontend, 'packages/sdk/.framework-state/writer.json'),
  ]) {
    mkdirSync(join(path, '..'), { recursive: true });
    writeFileSync(path, JSON.stringify(record) + '\n', { mode: 0o600 });
    chmodSync(path, 0o600);
  }
  const child = new EventEmitter();
  Object.assign(child, {
    pid: record.pid,
    exitCode: null,
    signalCode: null,
    connected: true,
    sent: [],
    killed: [],
    stdout: null,
    stderr: null,
    send(value, callback) {
      this.sent.push(value);
      callback?.();
    },
    kill(signal) {
      this.killed.push(signal);
      return true;
    },
  });
  const remove = () => rmSync(root, { recursive: true, force: true });
  return { app, guardLink, record, child, remove, createCopiedRuntimeHostBroker, stopTestChild };
}

test('host broker withholds ownership frames from strict proof consumers', async () => {
  const f = await fixture();
  try {
    const broker = f.createCopiedRuntimeHostBroker(f.child, { app: f.app, profile: 'admission' });
    const messages = [];
    broker.proof.on('message', (value) => messages.push(value));
    f.child.emit('message', { kind: 'runtime-lease', record: f.record });
    f.child.emit('message', {
      kind: 'runtime-family',
      pid: 34567,
      group: 34567,
      parentPid: f.child.pid,
      leaseNonce: f.record.nonce,
      incarnation: 'b'.repeat(32),
    });
    assert.deepEqual(messages, []);
    assert.equal(broker.capture.family().pid, 34567);
    broker.send({ id: 1, op: 'observe' });
    assert.deepEqual(f.child.sent, [{ id: 1, op: 'observe' }]);
    f.child.emit('message', {
      id: 1,
      active: 1,
      snapshots: 0,
      snapshotsSeen: 1,
      reads: 1,
      immutable: true,
      lateRefused: 0,
      lateAccepted: 0,
    });
    assert.equal(messages.length, 1);
    broker.assertHealthy();
  } finally {
    f.remove();
  }
});

test('changed or forged family evidence fails without proof delivery', async () => {
  const f = await fixture();
  try {
    const broker = f.createCopiedRuntimeHostBroker(f.child, { app: f.app, profile: 'none' });
    f.child.emit('message', { kind: 'runtime-lease', record: f.record });
    f.child.emit('message', {
      kind: 'runtime-family',
      pid: 34567,
      group: 34567,
      parentPid: f.child.pid,
      leaseNonce: 'c'.repeat(32),
      incarnation: 'b'.repeat(32),
    });
    assert.throws(() => broker.assertHealthy());
    assert.deepEqual(f.child.killed, ['SIGTERM']);
  } finally {
    f.remove();
  }
});

test('broker refuses a copied scripts link redirected away from the actual guard', async () => {
  const f = await fixture();
  try {
    const broker = f.createCopiedRuntimeHostBroker(f.child, { app: f.app, profile: 'none' });
    const other = join(f.app, '../../../other-scripts');
    mkdirSync(other);
    unlinkSync(f.guardLink);
    symlinkSync(other, f.guardLink, 'dir');
    f.child.emit('message', { kind: 'runtime-lease', record: f.record });
    assert.throws(() => broker.assertHealthy());
    assert.deepEqual(f.child.killed, ['SIGTERM']);
  } finally {
    f.remove();
  }
});

test('BFF broker projects raw consumer replies but refuses a later stale wire', async () => {
  const f = await fixture();
  try {
    const broker = f.createCopiedRuntimeHostBroker(f.child, { app: f.app, profile: 'bff' });
    const projected = [];
    broker.proof.on('message', (value) => projected.push(value));
    f.child.emit('message', { kind: 'runtime-lease', record: f.record });
    f.child.emit('message', {
      kind: 'runtime-family',
      pid: 34567,
      group: 34567,
      parentPid: f.child.pid,
      leaseNonce: f.record.nonce,
      incarnation: 'b'.repeat(32),
    });
    const snapshot = (now) => ({
      kind: 'snapshot',
      now,
      refreshes: 0,
      accounts: 0,
      administrations: {},
    });
    broker.send({ kind: 'advance', milliseconds: 0 });
    assert.deepEqual(f.child.sent[0], {
      kind: 'proof-command',
      sequence: 1,
      payload: { kind: 'advance', milliseconds: 0 },
    });
    f.child.emit('message', { kind: 'proof-reply', sequence: 1, payload: snapshot(0) });
    broker.send({ kind: 'advance', milliseconds: 60000 });
    f.child.emit('message', { kind: 'proof-reply', sequence: 2, payload: snapshot(60000) });
    assert.deepEqual(projected, [snapshot(0), snapshot(60000)]);
    broker.send({ kind: 'render-control', action: 'status' });
    assert.deepEqual(f.child.sent[2], {
      kind: 'proof-command',
      sequence: 3,
      payload: { kind: 'render-control', action: 'status' },
    });
    f.child.emit('message', {
      kind: 'proof-reply',
      sequence: 2,
      payload: { kind: 'render-state', held: 0, frames: 0, snapshots: null },
    });
    assert.throws(() => broker.assertHealthy());
    assert.equal(projected.length, 2);
    assert.deepEqual(f.child.killed, ['SIGTERM']);
  } finally {
    f.remove();
  }
});

test('ingress broker correlates concurrent fetches and projects the unchanged consumer shape', async () => {
  const f = await fixture();
  try {
    const broker = f.createCopiedRuntimeHostBroker(f.child, { app: f.app, profile: 'ingress' });
    const projected = [];
    broker.proof.on('message', (value) => projected.push(value));
    f.child.emit('message', { kind: 'runtime-lease', record: f.record });
    f.child.emit('message', {
      kind: 'runtime-family',
      pid: 34567,
      group: 34567,
      parentPid: f.child.pid,
      leaseNonce: f.record.nonce,
      incarnation: 'b'.repeat(32),
    });
    const fetch = (sequence, state) => ({
      kind: 'version-fetch',
      pid: 34567,
      sequence,
      state,
      errorKind: 'none',
      elapsedMilliseconds: 4,
    });
    f.child.emit('message', fetch(1, 'pending'));
    f.child.emit('message', fetch(2, 'pending'));
    f.child.emit('message', fetch(1, 'settled'));
    broker.assertHealthy();
    assert.deepEqual(projected, [
      {
        kind: 'version-fetch',
        pid: 34567,
        state: 'pending',
        errorKind: 'none',
        elapsedMilliseconds: 4,
      },
      {
        kind: 'version-fetch',
        pid: 34567,
        state: 'pending',
        errorKind: 'none',
        elapsedMilliseconds: 4,
      },
      {
        kind: 'version-fetch',
        pid: 34567,
        state: 'settled',
        errorKind: 'none',
        elapsedMilliseconds: 4,
      },
    ]);
    assert.ok(projected.every((value) => !Object.hasOwn(value, 'sequence')));
    f.child.emit('message', fetch(1, 'settled'));
    assert.throws(() => broker.assertHealthy());
    assert.equal(projected.length, 3);
    assert.deepEqual(f.child.killed, ['SIGTERM']);
  } finally {
    f.remove();
  }
});

test('sequential producer generations with reused PID retire before exact record reconciliation', async () => {
  const f = await fixture('development');
  try {
    const broker = f.createCopiedRuntimeHostBroker(f.child, { app: f.app, profile: 'none' });
    f.child.emit('message', { kind: 'runtime-lease', record: f.record });
    const producer = {
      kind: 'producer-family',
      pid: 34567,
      group: 34567,
      parentPid: f.child.pid,
      leaseNonce: f.record.nonce,
    };
    f.child.emit('message', { ...producer, ordinal: 1 });
    f.child.emit('message', { ...producer, ordinal: 2 });
    f.child.emit('message', {
      kind: 'runtime-family',
      pid: 45678,
      group: 45678,
      parentPid: f.child.pid,
      leaseNonce: f.record.nonce,
      incarnation: 'b'.repeat(32),
    });
    broker.assertHealthy();
    assert.deepEqual(
      broker.capture.producers().map((handle) => handle.ordinal),
      [1, 2],
    );
    f.child.exitCode = 0;
    f.child.emit('close', 0, null);
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
      assert.deepEqual(await f.stopTestChild(f.child), {
        forced: false,
        ownerCode: 0,
        ownerSignal: null,
      });
      assert.deepEqual(probes, [-23456, -34567, -45678]);
      assert.equal(broker.capture.retired(), true);
      assert.equal(broker.reconcile(), 'reconciled');
    } finally {
      process.kill = originalKill;
    }
  } finally {
    f.remove();
  }
});

test('replayed producer ordinal cannot be captured as another launch', async () => {
  const f = await fixture('development');
  try {
    const broker = f.createCopiedRuntimeHostBroker(f.child, { app: f.app, profile: 'none' });
    f.child.emit('message', { kind: 'runtime-lease', record: f.record });
    const producer = {
      kind: 'producer-family',
      ordinal: 1,
      pid: 34567,
      group: 34567,
      parentPid: f.child.pid,
      leaseNonce: f.record.nonce,
    };
    f.child.emit('message', producer);
    f.child.emit('message', producer);
    assert.throws(() => broker.assertHealthy());
    assert.deepEqual(f.child.killed, ['SIGTERM']);
  } finally {
    f.remove();
  }
});
