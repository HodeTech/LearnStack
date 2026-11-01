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

async function fixture() {
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
  const nonce = randomBytes(16).toString('hex');
  const record = { format: 1, pid: 23456, nonce, kind: 'production' };
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
    join(copyFrontend, 'packages/sdk/.framework-state/readers', nonce),
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
  return { app, guardLink, record, child, remove, createCopiedRuntimeHostBroker };
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
