// Copied-fixture evidence only. The host supplies its independently admitted union.
import assert from 'node:assert/strict';
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

import { routerPipeInventory } from './framework-router-generated-pipes.mjs';

const CASES = ['healthy-identity', 'healthy-gzip', 'healthy-deflate'];
const refuse = () => {
  throw new Error('Copied router pipe evidence refused');
};
const exact = (value, keys) =>
  value !== null &&
  typeof value === 'object' &&
  !Array.isArray(value) &&
  Object.keys(value).sort().join(',') === [...keys].sort().join(',');
const same = (a, b) =>
  ['dev', 'ino', 'size', 'mtimeNs', 'ctimeNs', 'mode', 'nlink', 'uid', 'gid'].every(
    (key) => a[key] === b[key],
  );

/** Each read owns a descriptor and retains immutable prefix/inode evidence across reads. */
function reader(path, maximum, immutable = false) {
  let previous;
  return () => {
    try {
      const before = lstatSync(path, { bigint: true });
      const valid = (stat) =>
        stat.isFile() &&
        !stat.isSymbolicLink() &&
        stat.nlink === 1n &&
        stat.uid === BigInt(process.getuid()) &&
        (stat.mode & 0o777n) === 0o600n &&
        stat.size >= 0n &&
        stat.size <= BigInt(maximum) &&
        stat.dev === before.dev &&
        stat.ino === before.ino;
      if (!valid(before) || realpathSync(path) !== path) refuse();
      const fd = openSync(path, constants.O_RDONLY | constants.O_NOFOLLOW);
      try {
        for (let attempt = 0; attempt < 3; attempt++) {
          const opened = fstatSync(fd, { bigint: true });
          if (!valid(opened)) refuse();
          const bytes = Buffer.alloc(Number(opened.size));
          const count = readSync(fd, bytes, 0, bytes.length, 0);
          const after = fstatSync(fd, { bigint: true }),
            named = lstatSync(path, { bigint: true });
          if (!valid(after) || !valid(named) || realpathSync(path) !== path) refuse();
          if (!same(opened, after) || !same(after, named)) continue;
          if (count !== bytes.length || (bytes.length && bytes.at(-1) !== 10)) refuse();
          if (
            previous &&
            (previous.dev !== named.dev ||
              previous.ino !== named.ino ||
              bytes.length < previous.bytes.length ||
              !bytes.subarray(0, previous.bytes.length).equals(previous.bytes) ||
              (immutable && (!same(previous.stat, named) || !bytes.equals(previous.bytes))))
          )
            refuse();
          previous = { dev: named.dev, ino: named.ino, stat: named, bytes };
          return bytes;
        }
        refuse();
      } finally {
        closeSync(fd);
      }
    } catch {
      refuse();
    }
  };
}
function rows(bytes, maximumRows, maximumLine) {
  const lines = bytes.length ? bytes.toString('utf8').slice(0, -1).split('\n') : [];
  if (
    lines.length > maximumRows ||
    lines.some((line) => Buffer.byteLength(line + '\n') > maximumLine)
  )
    refuse();
  return lines.map((line) => JSON.parse(line));
}

export function createRouterPipeObservation({ app, broker, union, compressionSha256 }) {
  const root = resolve(app, '../../..'),
    rootStat = lstatSync(root);
  const assertRoot = () => {
    const stat = lstatSync(root);
    if (
      realpathSync(root) !== root ||
      !stat.isDirectory() ||
      stat.isSymbolicLink() ||
      stat.uid !== process.getuid() ||
      (stat.mode & 0o777) !== 0o700 ||
      stat.dev !== rootStat.dev ||
      stat.ino !== rootStat.ino ||
      realpathSync(app) !== app ||
      app !== join(root, 'frontend/apps/web')
    )
      refuse();
  };
  assertRoot();
  broker.assertHealthy();
  const family = broker.capture.family(),
    lease = broker.capture.lease();
  if (
    !family ||
    !Object.isFrozen(family) ||
    !lease ||
    !Object.isFrozen(lease) ||
    !Object.isFrozen(union) ||
    union.format !== 1 ||
    union.files.length < 28 ||
    union.files.length > 155 ||
    family.group !== family.pid ||
    family.parentPid !== lease.pid ||
    family.leaseNonce !== lease.nonce ||
    !/^[a-f0-9]{64}$/.test(compressionSha256)
  )
    refuse();
  const expectedInventory = routerPipeInventory(union);
  const inventoryRead = reader(join(root, '.router-pipe-inventory.json'), 262144, true);
  const pipeRead = reader(join(root, '.router-pipe.events'), 384);
  const filterRead = reader(join(root, '.router-filter.events'), 768);
  const traceRead = reader(join(root, '.router-pipe-trace.events'), 16384);
  const attestedRead = reader(join(root, '.router-pipe-attested.events'), 1152);
  function inspect(terminal = false) {
    try {
      assertRoot();
      broker.assertHealthy();
      if (
        broker.capture.family() !== family ||
        broker.capture.lease() !== lease ||
        (terminal && broker.capture.retired() !== true)
      )
        refuse();
      const inventory = JSON.parse(inventoryRead());
      if (
        !exact(inventory, ['version', 'pid', 'inventory']) ||
        inventory.version !== 1 ||
        inventory.pid !== family.pid
      )
        refuse();
      assert.deepEqual(inventory.inventory, expectedInventory);
      const pipes = rows(pipeRead(), 3, 128);
      for (const [index, row] of pipes.entries())
        if (
          !exact(row, ['version', 'pid', 'sequence', 'case', 'index']) ||
          row.version !== 1 ||
          row.pid !== family.pid ||
          row.sequence !== index + 1 ||
          row.case !== CASES[index] ||
          !Number.isSafeInteger(row.index) ||
          !union.sites[row.index]
        )
          refuse();
      const filters = rows(filterRead(), 3, 256);
      for (const [index, row] of filters.entries())
        if (
          !exact(row, [
            'version',
            'pid',
            'sequence',
            'case',
            'subject',
            'subjectSha256',
            'kind',
            'headerClass',
          ]) ||
          row.version !== 1 ||
          row.pid !== family.pid ||
          row.sequence !== index + 1 ||
          row.case !== CASES[0] ||
          row.subject !== 'compression' ||
          row.subjectSha256 !== compressionSha256 ||
          row.kind !== 'filtered-header' ||
          row.headerClass !== 'array-one-text-plain'
        )
          refuse();
      if (filters.length > 1) refuse();
      const trace = rows(traceRead(), 128, 128);
      const scripts = new Set(),
        resolved = new Set(),
        sourced = new Set(),
        hit = new Map(),
        marked = new Map(),
        entries = new Map();
      for (const [index, row] of trace.entries()) {
        if (
          !exact(row, ['v', 'p', 'n', 'k', 's', 't', 'h', 'x']) ||
          row.v !== 1 ||
          row.p !== family.pid ||
          row.n !== index + 1 ||
          !['setup', 'prepared', 'case-0', 'case-1', 'case-2', 'closing'].includes(row.h) ||
          !Number.isSafeInteger(row.s) ||
          !Number.isSafeInteger(row.t) ||
          row.k === 'fault'
        )
          refuse();
        const file = union.files[row.s],
          key = row.s + ':' + row.t,
          request = row.h + ':' + key;
        if (!file) refuse();
        if (row.k === 'script') {
          if (row.t !== -1 || row.x !== 'ready' || scripts.has(row.s)) refuse();
          scripts.add(row.s);
        } else if (row.k === 'source') {
          if (row.t !== -1 || row.x !== 'match' || !scripts.has(row.s) || sourced.has(row.s))
            refuse();
          sourced.add(row.s);
        } else {
          if (!file.entries[row.t]) refuse();
          if (row.k === 'resolved') {
            if (row.x !== 'match' || !scripts.has(row.s) || resolved.has(key)) refuse();
            resolved.add(key);
          } else if (row.k === 'hit') {
            if (
              row.x !== 'known' ||
              !scripts.has(row.s) ||
              !resolved.has(key) ||
              !row.h.startsWith('case-')
            )
              refuse();
            hit.set(request, row.n);
            marked.delete(request);
          } else if (row.k === 'mark') {
            if (!['true', 'false'].includes(row.x) || !hit.has(request) || !sourced.has(row.s))
              refuse();
            marked.set(request, row.x);
          } else if (row.k === 'entry') {
            if (row.x !== 'router-seen' || marked.get(request) !== 'true' || entries.has(row.h))
              refuse();
            entries.set(row.h, key);
            marked.delete(request);
          } else refuse();
        }
      }
      for (const [index, row] of pipes.entries()) {
        const site = union.sites[row.index];
        if (entries.get('case-' + index) !== site.fileIndex + ':' + site.entryIndex) refuse();
      }
      const attestations = rows(attestedRead(), 3, 384);
      const scriptIds = new Map();
      for (const [index, row] of attestations.entries()) {
        if (
          !exact(row, [
            'version',
            'pid',
            'sequence',
            'case',
            'index',
            'scriptId',
            'sha256',
            'line',
            'column',
          ]) ||
          row.version !== 1 ||
          row.pid !== family.pid ||
          row.sequence !== index + 1 ||
          row.case !== CASES[index] ||
          !Number.isSafeInteger(row.index) ||
          !union.sites[row.index] ||
          !/^[0-9]{1,16}$/.test(row.scriptId) ||
          !Number.isSafeInteger(row.line) ||
          row.line < 0 ||
          !Number.isSafeInteger(row.column) ||
          row.column < 0
        )
          refuse();
        const site = union.sites[row.index],
          point = site.point;
        if (
          row.sha256 !== site.sha256 ||
          row.line < point.line ||
          row.line > point.endLine ||
          (row.line === point.line && row.column < point.column) ||
          (row.line === point.endLine && row.column > point.endColumn) ||
          (scriptIds.has(site.fileIndex) && scriptIds.get(site.fileIndex) !== row.scriptId) ||
          [...scriptIds].some(
            ([file, script]) => file !== site.fileIndex && script === row.scriptId,
          )
        )
          refuse();
        scriptIds.set(site.fileIndex, row.scriptId);
        if (pipes[index] && pipes[index].index !== row.index) refuse();
      }
      if (attestations.length !== pipes.length) refuse();
      if (terminal && (pipes.length !== 3 || filters.length !== 1 || entries.size !== 3)) refuse();
      assertRoot();
      return Object.freeze({
        pipes: Object.freeze(pipes),
        filters: Object.freeze(filters),
        trace: Object.freeze(trace),
        attestations: Object.freeze(attestations),
        inventorySha256: expectedInventory.sha256,
        manifestSha256: union.manifestSha256,
      });
    } catch {
      refuse();
    }
  }
  return Object.freeze({
    inspect,
    assertCase(name) {
      const observation = inspect();
      if (
        !CASES.includes(name) ||
        !observation.pipes.some((row) => row.case === name) ||
        observation.filters.length !== 1
      )
        refuse();
      return observation;
    },
    assertClosed() {
      return inspect(true);
    },
  });
}
