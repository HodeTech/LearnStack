// Fixture-owned channel split: ownership witnesses never enter proof consumers.
import { EventEmitter } from 'node:events';
import {
  closeSync,
  fstatSync,
  lstatSync,
  openSync,
  readFileSync,
  realpathSync,
  unlinkSync,
} from 'node:fs';
import { join, resolve } from 'node:path';

import { createCopiedHostProofValidator } from './framework-proof-relay.mjs';
import { markRuntimeTestChild } from './stop-test-child.mjs';
import { FRAMEWORK_FRONTEND_ROOT } from '../../../scripts/framework-installation.mjs';

const exact = (value, keys) =>
  value !== null &&
  typeof value === 'object' &&
  !Array.isArray(value) &&
  Object.keys(value).sort().join(',') === [...keys].sort().join(',');
const fail = () => {
  throw new Error('Copied runtime host evidence refused');
};

function readExactFile(path) {
  const before = lstatSync(path);
  if (
    !before.isFile() ||
    before.isSymbolicLink() ||
    before.nlink !== 1 ||
    before.uid !== process.getuid() ||
    (before.mode & 0o777) !== 0o600 ||
    before.size > 4096 ||
    realpathSync(path) !== path
  )
    fail();
  const descriptor = openSync(path, 'r');
  try {
    const opened = fstatSync(descriptor);
    if (
      !opened.isFile() ||
      opened.dev !== before.dev ||
      opened.ino !== before.ino ||
      opened.nlink !== 1 ||
      opened.uid !== before.uid ||
      (opened.mode & 0o777) !== 0o600 ||
      opened.size !== before.size
    )
      fail();
    const bytes = readFileSync(descriptor);
    const after = fstatSync(descriptor);
    if (
      after.size !== bytes.length ||
      after.mtimeMs !== opened.mtimeMs ||
      after.ctimeMs !== opened.ctimeMs
    )
      fail();
    return bytes.toString('utf8');
  } finally {
    closeSync(descriptor);
  }
}

function present(path) {
  try {
    lstatSync(path);
    return true;
  } catch (error) {
    if (error.code === 'ENOENT') return false;
    throw error;
  }
}

function ownedState(path) {
  const entry = lstatSync(path);
  if (
    !entry.isDirectory() ||
    entry.isSymbolicLink() ||
    entry.uid !== process.getuid() ||
    (entry.mode & 0o777) !== 0o700 ||
    realpathSync(path) !== path
  )
    fail();
}

function captureLeaseFiles(app, record) {
  const appRoot = realpathSync(app);
  const guardLink = resolve(appRoot, '../../scripts');
  if (!lstatSync(guardLink).isSymbolicLink()) fail();
  const frontend = resolve(realpathSync(guardLink), '..');
  if (frontend !== FRAMEWORK_FRONTEND_ROOT) fail();
  const sdk = resolve(appRoot, '../../packages/sdk');
  for (const root of [frontend, appRoot, sdk]) {
    ownedState(join(root, '.framework-state'));
    ownedState(join(root, '.framework-state/readers'));
  }
  const paths = [
    join(frontend, '.framework-state/readers', record.nonce),
    join(appRoot, '.framework-state/writer.json'),
    join(
      sdk,
      '.framework-state',
      record.kind === 'production' ? `readers/${record.nonce}` : 'writer.json',
    ),
  ];
  return Object.freeze(
    paths.map((path) => {
      const bytes = readExactFile(path);
      const parsed = JSON.parse(bytes);
      if (
        !exact(parsed, ['format', 'pid', 'nonce', 'kind']) ||
        parsed.format !== record.format ||
        parsed.pid !== record.pid ||
        parsed.nonce !== record.nonce ||
        parsed.kind !== record.kind
      )
        fail();
      return Object.freeze({ path, bytes });
    }),
  );
}

/** Attach immediately after the fixture's own detached owner spawn. */
export function createCopiedRuntimeHostBroker(child, { app, profile }) {
  const capture = markRuntimeTestChild(child);
  const validator = createCopiedHostProofValidator(profile);
  const proof = new EventEmitter();
  const metadata = new EventEmitter();
  let records;
  let fault = null;
  let nextBffSequence = 1;
  const failClosed = () => {
    fault ??= new Error('Copied runtime host protocol refused');
    metadata.emit('fault', fault);
    proof.emit('fault', fault);
    if (child.exitCode === null && child.signalCode === null) child.kill('SIGTERM');
  };
  const receive = (value) => {
    if (fault) return;
    try {
      if (value?.kind === 'runtime-lease') {
        if (!exact(value, ['kind', 'record'])) fail();
        capture.captureLease(value.record);
        records = captureLeaseFiles(app, value.record);
        metadata.emit('lease', capture.lease());
      } else if (value?.kind === 'producer-family') {
        const { kind, ...handle } = value;
        capture.captureProducerFamily(handle);
        metadata.emit('producer', handle);
      } else if (value?.kind === 'runtime-family') {
        const { kind, ...handle } = value;
        capture.captureFamily(handle);
        metadata.emit('family', capture.family());
      } else {
        const family = capture.family();
        if (!family) fail();
        validator.observation(value, family.pid);
        proof.emit(
          'message',
          profile === 'bff' && value.kind === 'proof-reply' ? value.payload : value,
        );
      }
    } catch {
      failClosed();
    }
  };
  child.on('message', receive);
  child.once('close', (...args) => {
    metadata.emit('close', ...args);
    proof.emit('close', ...args);
  });
  child.once('error', (error) => {
    metadata.emit('child-error', error);
    proof.emit('child-error', error);
  });
  Object.defineProperties(proof, {
    pid: { get: () => capture.family()?.pid },
    exitCode: { get: () => child.exitCode },
    signalCode: { get: () => child.signalCode },
    stderr: { get: () => child.stderr },
    stdout: { get: () => child.stdout },
  });
  return Object.freeze({
    proof,
    metadata,
    capture,
    send(value) {
      if (fault || !capture.family() || !child.connected) fail();
      const wire =
        profile === 'bff'
          ? Object.freeze({ kind: 'proof-command', sequence: nextBffSequence, payload: value })
          : value;
      validator.command(wire);
      if (profile === 'bff') nextBffSequence++;
      child.send(wire, (error) => {
        if (error) failClosed();
      });
    },
    assertHealthy() {
      if (fault) throw fault;
    },
    records() {
      return records ?? null;
    },
    /** Only isolated copied roots may reconcile exact unchanged captured records. */
    reconcile() {
      if (fault || !records || !capture.retired()) fail();
      // stopTestChild has permanently retired each captured family. A live or
      // changed record cannot be removed by this fixture-only action.
      if (child.exitCode === null && child.signalCode === null) fail();
      const existing = records.map(({ path }) => present(path));
      if (existing.every((value) => !value)) {
        if (child.exitCode !== 0 || child.signalCode !== null) fail();
        return 'already-released';
      }
      if (existing.some((value) => !value)) fail();
      for (const { path, bytes } of records) if (readExactFile(path) !== bytes) fail();
      for (const { path } of records) unlinkSync(path);
      return 'reconciled';
    },
  });
}
