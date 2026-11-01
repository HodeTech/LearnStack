// PRIVATE copied-fixture host. Observations grant no release authority.
import {
  constants,
  closeSync,
  fstatSync,
  lstatSync,
  openSync,
  readSync,
  realpathSync,
} from 'node:fs';
import { join, resolve } from 'node:path';
import { TextDecoder } from 'node:util';

import { validateCopiedOverlapRows } from './framework-overlap-proof-rows.mjs';

const refuse = () => {
  throw new Error('Copied overlap evidence refused');
};
function ownedRoot(root) {
  const entry = lstatSync(root);
  if (
    !entry.isDirectory() ||
    entry.isSymbolicLink() ||
    entry.uid !== process.getuid() ||
    (entry.mode & 0o777) !== 0o700 ||
    realpathSync(root) !== root
  )
    refuse();
}
export function createCopiedOverlapHost(options) {
  try {
    return createHost(options);
  } catch {
    refuse();
  }
}
function createHost({ app, broker }) {
  const appRoot = realpathSync(app),
    root = resolve(appRoot, '../../..');
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
    records.length !== 3 ||
    records.some((item) => typeof item?.path !== 'string') ||
    new Set(records.map((item) => item.path)).size !== 3 ||
    !records.some((item) => item.path === join(appRoot, '.framework-state/writer.json'))
  )
    refuse();
  const path = join(root, '.overlap-proof.events'),
    before = lstatSync(path);
  const valid = (entry) =>
    entry.isFile() &&
    !entry.isSymbolicLink() &&
    entry.nlink === 1 &&
    entry.uid === process.getuid() &&
    (entry.mode & 0o777) === 0o600 &&
    entry.size <= 65536 &&
    entry.dev === before.dev &&
    entry.ino === before.ino;
  if (!valid(before) || realpathSync(path) !== path) refuse();
  const fd = openSync(path, constants.O_RDONLY | constants.O_NOFOLLOW);
  try {
    if (!valid(fstatSync(fd))) refuse();
  } catch (error) {
    closeSync(fd);
    throw error;
  }
  let prior = Buffer.alloc(0),
    closed = false;
  const assertBinding = () => {
    try {
      if (closed) refuse();
      broker.assertHealthy();
      if (broker.capture.family() !== family || broker.capture.lease() !== lease) refuse();
      ownedRoot(root);
      if (realpathSync(app) !== appRoot || realpathSync(appRoot) !== appRoot) refuse();
    } catch {
      refuse();
    }
  };
  return Object.freeze({
    events() {
      assertBinding();
      let minimum = prior.length;
      for (let attempt = 0; attempt < 3; attempt++) {
        const opened = fstatSync(fd);
        if (!valid(opened) || opened.size < minimum) refuse();
        const bytes = Buffer.alloc(opened.size);
        const count = readSync(fd, bytes, 0, bytes.length, 0);
        const after = fstatSync(fd),
          current = lstatSync(path);
        if (
          !valid(after) ||
          !valid(current) ||
          realpathSync(path) !== path ||
          after.size < opened.size ||
          current.size < after.size
        )
          refuse();
        minimum = current.size;
        if (
          opened.size !== after.size ||
          after.size !== current.size ||
          opened.mtimeMs !== after.mtimeMs ||
          opened.ctimeMs !== after.ctimeMs ||
          after.mtimeMs !== current.mtimeMs ||
          after.ctimeMs !== current.ctimeMs
        )
          continue;
        if (count !== opened.size || !bytes.subarray(0, prior.length).equals(prior)) refuse();
        const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
        const rows = text ? validateCopiedOverlapRows(text, family.pid) : Object.freeze([]);
        assertBinding();
        prior = bytes;
        return rows;
      }
      refuse();
    },
    close() {
      if (closed) refuse();
      closed = true;
      closeSync(fd);
    },
  });
}
