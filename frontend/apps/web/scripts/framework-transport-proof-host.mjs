// Copied proof host codec. These observations supply no cleanup authority.
import {
  constants,
  closeSync,
  fstatSync,
  lstatSync,
  openSync,
  readSync,
  realpathSync,
  writeSync,
} from 'node:fs';
import { join, resolve } from 'node:path';
const CASES = new Set([
  'healthy-gzip',
  'healthy-deflate',
  'healthy-identity',
  'fence-gzip',
  'fence-deflate',
]);
const REASONS = new Set([
  'filtered',
  'no transform',
  'size below threshold',
  'already encoded',
  'HEAD request',
  'not acceptable',
  'owned allocation sealed',
]);
const EMPTY = new Set([
  'router',
  'response-finish',
  'response-close',
  'handler-ok',
  'handler-failed',
]);
const exact = (value, keys) =>
  value !== null &&
  typeof value === 'object' &&
  !Array.isArray(value) &&
  Object.keys(value).sort().join(',') === [...keys].sort().join(',');
const FAILURE_REASONS = new Set([
  'host-file-identity',
  'host-file-changing',
  'host-file-io',
  'host-schema',
  'host-binding',
  'handler-failed',
  'unexpected-selection',
]);
class TransportProofRefusal extends Error {
  constructor(reason) {
    super('Copied transport evidence refused');
    this.reason = reason;
  }
}
const fail = (reason = 'host-binding') => {
  throw new TransportProofRefusal(reason);
};
export function transportProofFailureReason(error) {
  return error instanceof TransportProofRefusal && FAILURE_REASONS.has(error.reason)
    ? error.reason
    : 'unknown';
}
function ownedRoot(path) {
  const stat = lstatSync(path);
  if (
    !stat.isDirectory() ||
    stat.isSymbolicLink() ||
    stat.uid !== process.getuid() ||
    (stat.mode & 0o777) !== 0o700 ||
    realpathSync(path) !== path
  )
    fail();
}
function readEventFile(path) {
  try {
    const before = lstatSync(path);
    const valid = (entry) =>
      entry.isFile() &&
      !entry.isSymbolicLink() &&
      entry.nlink === 1 &&
      entry.uid === process.getuid() &&
      (entry.mode & 0o777) === 0o600 &&
      entry.size <= 25600 &&
      entry.dev === before.dev &&
      entry.ino === before.ino;
    if (!valid(before) || realpathSync(path) !== path) fail('host-file-identity');
    const fd = openSync(path, constants.O_RDONLY | constants.O_NOFOLLOW);
    try {
      let minimumSize = before.size;
      for (let attempt = 0; attempt < 3; attempt++) {
        const opened = fstatSync(fd);
        if (!valid(opened)) fail('host-file-identity');
        if (opened.size < minimumSize) fail('host-file-changing');
        const bytes = Buffer.alloc(opened.size);
        const length = readSync(fd, bytes, 0, bytes.length, 0);
        const after = fstatSync(fd);
        const current = lstatSync(path);
        if (!valid(after) || !valid(current) || realpathSync(path) !== path)
          fail('host-file-identity');
        if (after.size < opened.size || current.size < after.size) fail('host-file-changing');
        minimumSize = current.size;
        if (
          opened.size !== after.size ||
          after.size !== current.size ||
          opened.mtimeMs !== after.mtimeMs ||
          opened.ctimeMs !== after.ctimeMs ||
          after.mtimeMs !== current.mtimeMs ||
          after.ctimeMs !== current.ctimeMs
        )
          continue;
        if (length !== opened.size) fail('host-file-io');
        return bytes.toString('utf8');
      }
      fail('host-file-changing');
    } finally {
      closeSync(fd);
    }
  } catch (error) {
    if (error instanceof TransportProofRefusal) throw error;
    fail('host-file-io');
  }
}
/** Bind to the broker's already-authenticated immutable family, not a PID file. */
export function createCopiedTransportHost({ app, broker }) {
  const appRoot = realpathSync(app),
    root = resolve(appRoot, '../../..');
  ownedRoot(root);
  if (
    appRoot !== join(root, 'frontend/apps/web') ||
    typeof broker?.assertHealthy !== 'function' ||
    typeof broker?.capture?.family !== 'function' ||
    typeof broker?.capture?.lease !== 'function' ||
    typeof broker?.records !== 'function'
  )
    fail();
  broker.assertHealthy();
  const family = broker.capture.family(),
    lease = broker.capture.lease(),
    records = broker.records();
  if (
    !family ||
    !Object.isFrozen(family) ||
    !lease ||
    !Object.isFrozen(lease) ||
    !Number.isSafeInteger(family.pid) ||
    family.pid <= 0 ||
    family.group !== family.pid ||
    family.parentPid !== lease.pid ||
    family.leaseNonce !== lease.nonce ||
    !/^[0-9a-f]{32}$/.test(family.incarnation ?? '') ||
    !Array.isArray(records) ||
    !records.some((item) => item.path === join(appRoot, '.framework-state/writer.json'))
  )
    fail();
  const assertBinding = () => {
    try {
      broker.assertHealthy();
    } catch {
      fail('host-binding');
    }
    if (broker.capture.family() !== family || broker.capture.lease() !== lease) fail();
  };
  return Object.freeze({
    arm(name) {
      assertBinding();
      if (!CASES.has(name)) fail();
      const path = join(root, '.transport-proof.arm');
      const fd = openSync(
        path,
        constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | constants.O_NOFOLLOW,
        0o600,
      );
      try {
        const value = name + '\n';
        if (writeSync(fd, value) !== Buffer.byteLength(value)) fail();
      } finally {
        closeSync(fd);
      }
    },
    events() {
      assertBinding();
      const text = readEventFile(join(root, '.transport-proof.events'));
      if (!text) return Object.freeze([]);
      if (!text.endsWith('\n')) fail('host-schema');
      const records = text
        .slice(0, -1)
        .split('\n')
        .map((line, index) => {
          if (Buffer.byteLength(line) > 255) fail('host-schema');
          let value;
          try {
            value = JSON.parse(line);
          } catch {
            fail('host-schema');
          }
          if (
            !exact(value, ['version', 'pid', 'sequence', 'case', 'kind', 'value']) ||
            value.version !== 1 ||
            value.pid !== family.pid ||
            value.sequence !== index + 1 ||
            !CASES.has(value.case)
          )
            fail('host-schema');
          if (EMPTY.has(value.kind)) {
            if (value.value !== null) fail('host-schema');
          } else if (value.kind === 'allocation') {
            if (!['gzip', 'deflate'].includes(value.value) || !value.case.endsWith(value.value))
              fail('host-schema');
          } else if (value.kind === 'encoder-close') {
            if (!['ended', 'unended'].includes(value.value)) fail('host-schema');
          } else if (value.kind === 'none') {
            if (!REASONS.has(value.value)) fail('host-schema');
          } else fail('host-schema');
          return Object.freeze(value);
        });
      assertBinding();
      return Object.freeze(records);
    },
  });
}

/** Evaluate only already-validated observations; settlement never grants cleanup credit. */
export function copiedTransportEventsComplete(name, rows) {
  if (!CASES.has(name) || !Array.isArray(rows) || rows.some((row) => row.case !== name))
    fail('host-schema');
  const kinds = rows.map((row) => row.kind);
  if (kinds.includes('handler-failed')) fail('handler-failed');
  if (name !== 'healthy-identity' && kinds.includes('none')) fail('unexpected-selection');
  if (name === 'healthy-identity' && kinds.includes('allocation')) fail('unexpected-selection');
  return (
    kinds.includes('handler-ok') &&
    kinds.includes('response-finish') &&
    kinds.includes('response-close') &&
    (name === 'healthy-identity' ? kinds.includes('none') : kinds.includes('encoder-close'))
  );
}
