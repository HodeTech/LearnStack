// The fixed copied HTTP port is an observation, never cleanup authority.
import {
  closeSync,
  constants,
  fstatSync,
  lstatSync,
  openSync,
  readSync,
  realpathSync,
} from 'node:fs';
import { join, resolve } from 'node:path';

import { createRouterPipeObservation } from './framework-router-pipe-host.mjs';

const exact = (value, keys) =>
  value !== null &&
  typeof value === 'object' &&
  !Array.isArray(value) &&
  Object.keys(value).sort().join(',') === [...keys].sort().join(',');
const refuse = () => {
  throw new Error('Copied router HTTP port refused');
};
function ownedRoot(path) {
  const entry = lstatSync(path);
  if (
    !entry.isDirectory() ||
    entry.isSymbolicLink() ||
    entry.uid !== process.getuid() ||
    (entry.mode & 0o777) !== 0o700 ||
    realpathSync(path) !== path
  )
    refuse();
}
function exactPortFile(path) {
  try {
    const before = lstatSync(path);
    const valid = (entry) =>
      entry.isFile() &&
      !entry.isSymbolicLink() &&
      entry.nlink === 1 &&
      entry.uid === process.getuid() &&
      (entry.mode & 0o777) === 0o600 &&
      entry.size > 0 &&
      entry.size <= 96 &&
      entry.dev === before.dev &&
      entry.ino === before.ino;
    if (!valid(before) || realpathSync(path) !== path) refuse();
    const fd = openSync(path, constants.O_RDONLY | constants.O_NOFOLLOW);
    try {
      const opened = fstatSync(fd);
      if (!valid(opened) || opened.size !== before.size) refuse();
      const bytes = Buffer.alloc(opened.size);
      const count = readSync(fd, bytes, 0, bytes.length, 0);
      const after = fstatSync(fd);
      const current = lstatSync(path);
      if (
        count !== bytes.length ||
        !valid(after) ||
        !valid(current) ||
        after.size !== opened.size ||
        current.size !== after.size ||
        opened.mtimeMs !== after.mtimeMs ||
        opened.ctimeMs !== after.ctimeMs ||
        after.mtimeMs !== current.mtimeMs ||
        after.ctimeMs !== current.ctimeMs ||
        realpathSync(path) !== path
      )
        refuse();
      const text = bytes.toString('utf8');
      if (!text.endsWith('\n') || text.indexOf('\n') !== text.length - 1) refuse();
      const value = JSON.parse(text);
      if (
        !exact(value, ['version', 'pid', 'port']) ||
        value.version !== 1 ||
        !Number.isSafeInteger(value.pid) ||
        value.pid <= 0 ||
        !Number.isSafeInteger(value.port) ||
        value.port < 1 ||
        value.port > 65535
      )
        refuse();
      return Object.freeze({
        value: Object.freeze(value),
        bytes: text,
        dev: before.dev,
        ino: before.ino,
        size: before.size,
        uid: before.uid,
        nlink: before.nlink,
        mode: before.mode & 0o777,
        mtimeMs: before.mtimeMs,
        ctimeMs: before.ctimeMs,
      });
    } finally {
      closeSync(fd);
    }
  } catch {
    refuse();
  }
}
function samePortRecord(captured, current) {
  return (
    current.bytes === captured.bytes &&
    current.dev === captured.dev &&
    current.ino === captured.ino &&
    current.size === captured.size &&
    current.uid === captured.uid &&
    current.nlink === captured.nlink &&
    current.mode === captured.mode &&
    current.mtimeMs === captured.mtimeMs &&
    current.ctimeMs === captured.ctimeMs &&
    current.value.version === captured.value.version &&
    current.value.pid === captured.value.pid &&
    current.value.port === captured.value.port
  );
}
/** Capture only the already-authenticated runtime family; never trust a path from the sidecar. */
export function createCopiedRouterHttpPort({ app, broker, union, compressionSha256 }) {
  try {
    const appRoot = realpathSync(app);
    const root = resolve(appRoot, '../../..');
    ownedRoot(root);
    if (
      app !== appRoot ||
      appRoot !== join(root, 'frontend/apps/web') ||
      typeof broker?.assertHealthy !== 'function' ||
      typeof broker?.capture?.family !== 'function' ||
      typeof broker?.capture?.lease !== 'function' ||
      typeof broker?.records !== 'function'
    )
      refuse();
    broker.assertHealthy();
    const family = broker.capture.family();
    const lease = broker.capture.lease();
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
      !broker
        .records()
        .some((record) => record.path === join(appRoot, '.framework-state/writer.json'))
    )
      refuse();
    const portPath = join(root, '.router-http.port');
    const captured = exactPortFile(portPath);
    if (captured.value.pid !== family.pid) refuse();
    const assertBinding = () => {
      try {
        broker.assertHealthy();
        if (broker.capture.family() !== family || broker.capture.lease() !== lease) refuse();
        ownedRoot(root);
        if (realpathSync(app) !== appRoot || realpathSync(appRoot) !== appRoot) refuse();
        if (!samePortRecord(captured, exactPortFile(portPath))) refuse();
      } catch {
        refuse();
      }
    };
    assertBinding();
    const observation = createRouterPipeObservation({
      app: appRoot,
      broker,
      union,
      compressionSha256,
    });
    return Object.freeze({
      get port() {
        assertBinding();
        return captured.value.port;
      },
      assertBinding,
      family,
      assertCase: observation.assertCase,
      assertClosed: observation.assertClosed,
      inspect: observation.inspect,
      pipeEvents() {
        assertBinding();
        try {
          const rows = observation.inspect().pipes;
          assertBinding();
          return rows;
        } catch {
          refuse();
        }
      },
    });
  } catch {
    refuse();
  }
}
