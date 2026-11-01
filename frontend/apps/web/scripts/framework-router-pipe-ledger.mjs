// Copied-observer finite trace. Records observations, never grants authority.
import {
  constants,
  closeSync,
  fstatSync,
  lstatSync,
  openSync,
  realpathSync,
  writeSync,
} from 'node:fs';
import { join, resolve } from 'node:path';

const VALUES = Object.freeze({
  script: ['early', 'ready', 'duplicate'],
  resolved: ['match', 'range', 'duplicate'],
  hit: ['known', 'unknown', 'multiple'],
  mark: ['true', 'false', 'unavailable'],
  source: ['match', 'mismatch'],
  entry: ['router-seen', 'router-missing', 'duplicate'],
  fault: [
    'early-script',
    'duplicate-script',
    'unknown-breakpoint',
    'duplicate-resolution',
    'range-mismatch',
    'mark-unavailable',
    'pipe-before-router',
    'duplicate-pipe',
    'source-mismatch',
    'close-active',
    'incomplete-cases',
    'ledger-limit',
    'ledger-shape',
  ],
});
const PHASES = new Set(['setup', 'prepared', 'case-0', 'case-1', 'case-2', 'closing']);
const refuse = () => {
  throw new Error('Copied pipe trace refused');
};

export function createGeneratedPipeLedger(framework, pointCounts) {
  if (
    !framework ||
    !Object.isFrozen(framework) ||
    !Object.isFrozen(framework.identity) ||
    framework.mode !== 'production'
  )
    refuse();
  const identity = framework.identity;
  const root = resolve(identity.appRoot, '../../..');
  const rootStat = lstatSync(root);
  if (
    realpathSync(root) !== root ||
    !rootStat.isDirectory() ||
    rootStat.isSymbolicLink() ||
    rootStat.uid !== process.getuid() ||
    (rootStat.mode & 0o777) !== 0o700 ||
    identity.appRoot !== join(root, 'frontend/apps/web') ||
    realpathSync(identity.appRoot) !== identity.appRoot ||
    !Array.isArray(pointCounts) ||
    pointCounts.length < 27 ||
    pointCounts.length > 155 ||
    !pointCounts.every((count) => Number.isSafeInteger(count) && count >= 0 && count <= 8)
  )
    refuse();
  pointCounts = Object.freeze([...pointCounts]);
  const descriptor = openSync(
    join(root, '.router-pipe-trace.events'),
    constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | constants.O_NOFOLLOW,
    0o600,
  );
  try {
    const file = fstatSync(descriptor),
      currentRoot = lstatSync(root);
    if (
      !file.isFile() ||
      file.nlink !== 1 ||
      file.uid !== process.getuid() ||
      (file.mode & 0o777) !== 0o600 ||
      currentRoot.dev !== rootStat.dev ||
      currentRoot.ino !== rootStat.ino ||
      currentRoot.uid !== process.getuid() ||
      (currentRoot.mode & 0o777) !== 0o700 ||
      realpathSync(root) !== root
    )
      refuse();
  } catch (error) {
    closeSync(descriptor);
    throw error;
  }
  let sequence = 0,
    failed = false,
    closed = false,
    firstFault;
  function append(kind, subject, point, phase, value) {
    const line =
      JSON.stringify({
        v: 1,
        p: process.pid,
        n: sequence + 1,
        k: kind,
        s: subject,
        t: point,
        h: phase,
        x: value,
      }) + '\n';
    if (sequence >= 128 || Buffer.byteLength(line) > 128) {
      failed = true;
      return false;
    }
    try {
      if (writeSync(descriptor, line) !== Buffer.byteLength(line)) {
        failed = true;
        return false;
      }
      sequence++;
      return true;
    } catch {
      failed = true;
      return false;
    }
  }
  function fault(reason, phase = 'closing') {
    if (closed || firstFault !== undefined) return false;
    failed = true;
    firstFault = VALUES.fault.includes(reason) ? reason : 'ledger-shape';
    return append('fault', -1, -1, PHASES.has(phase) ? phase : 'closing', firstFault);
  }
  return Object.freeze({
    record(kind, subject, point, phase, value) {
      if (closed || failed) return false;
      const fileOnly = ['script', 'source'].includes(kind);
      if (
        !Object.hasOwn(VALUES, kind) ||
        kind === 'fault' ||
        !VALUES[kind].includes(value) ||
        !PHASES.has(phase) ||
        !Number.isSafeInteger(subject) ||
        subject < 0 ||
        subject >= pointCounts.length ||
        !Number.isSafeInteger(point) ||
        (fileOnly ? point !== -1 : point < 0 || point >= pointCounts[subject])
      ) {
        fault('ledger-shape', phase);
        return false;
      }
      // Reserve row 128 for the first diagnostic fault, including overflow.
      if (sequence >= 127) {
        fault('ledger-limit', phase);
        return false;
      }
      return append(kind, subject, point, phase, value);
    },
    fault,
    summary() {
      return Object.freeze({ rows: sequence, failed, firstFault: firstFault ?? null });
    },
    close() {
      if (closed) return;
      closed = true;
      closeSync(descriptor);
    },
  });
}
