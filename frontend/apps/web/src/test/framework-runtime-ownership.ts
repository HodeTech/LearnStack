import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { lstatSync, mkdtempSync, readFileSync, readdirSync, rmdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { closedFrameworkProofSync } from './framework-proof-closed-boundary';

type Group = 'startup' | 'workers' | 'recovery';
type Mode = 'production' | 'development';
const expectedCases: Record<Group, readonly (readonly [string, Mode])[]> = {
  startup: [
    ['ready-term', 'production'],
    ['ready-int', 'production'],
    ['prepare-term', 'production'],
    ['prepare-int', 'production'],
    ['prepare-watchdog', 'production'],
    ['registration-release', 'production'],
    ['registration-watchdog', 'production'],
    ['ready-term', 'development'],
    ['ready-int', 'development'],
    ['prepare-term', 'development'],
    ['prepare-int', 'development'],
    ['prepare-watchdog', 'development'],
    ['prehello-term', 'production'],
    ['prehello-int', 'production'],
    ['prehello-child-term', 'production'],
    ['prehello-child-int', 'production'],
    ['prehello-owner-death', 'production'],
  ],
  workers: [
    ['worker-positive', 'development'],
    ['worker-exit-zero', 'development'],
    ['worker-exit-one', 'development'],
    ['worker-self-kill', 'development'],
    ['worker-ordered-stop', 'development'],
  ],
  recovery: [
    ['prepare-disconnect', 'production'],
    ['prepare-owner-death', 'production'],
    ['prepare-disconnect', 'development'],
    ['prepare-owner-death', 'development'],
    ['worker-owner-death', 'development'],
    ['ready-term', 'production'],
    ['ready-term', 'development'],
  ],
};
const eventNames = [
  'native-imported',
  'prepare-entered',
  'prepare-released',
  'prepare-called',
  'prepare-settled',
  'stop-consumed',
  'admission-stop',
  'requests-stop',
  'sessions-stop',
  'next-close-called',
  'next-close-settled',
  'native-listen',
  'watchdog',
  'registration-entered',
  'registration-stop-latched',
  'registration-released',
  'direct-fault',
  'prehello-entered',
  'prehello-released',
  'prehello-stop-observed',
  'prehello-signal-observed',
  'prehello-parent-lost',
  'worker-entered',
  'worker-returned',
  'owner-receipt',
];
function object(value: unknown): Record<string, unknown> {
  assert.ok(value !== null && typeof value === 'object' && !Array.isArray(value));
  return value as Record<string, unknown>;
}
function exact(value: unknown, keys: readonly string[]): Record<string, unknown> {
  const record = object(value);
  assert.deepEqual(Object.keys(record).sort(), [...keys].sort());
  return record;
}
function integer(
  value: unknown,
  min: number,
  max = Number.MAX_SAFE_INTEGER,
): asserts value is number {
  assert.ok(
    typeof value === 'number' && Number.isSafeInteger(value) && value >= min && value <= max,
  );
}
function hash(value: unknown, length = 64) {
  assert.ok(typeof value === 'string' && new RegExp(`^[0-9a-f]{${length}}$`).test(value));
}
function ownerReceipt(value: unknown) {
  const flags = [
    'handshakeValidated',
    'listenerReady',
    'requestedStop',
    'startupCancelled',
    'parentLost',
    'directChildClosed',
    'groupAbsent',
  ];
  const receipt = exact(value, ['code', 'signal', 'fault', ...flags]);
  for (const flag of flags) assert.equal(typeof receipt[flag], 'boolean');
  if (receipt.code !== null) integer(receipt.code, 0, 255);
  assert.ok([null, 'SIGTERM', 'SIGINT', 'SIGKILL'].includes(receipt.signal as string | null));
  assert.ok(
    [
      null,
      'startup-fault',
      'listener-fault',
      'protocol-fault',
      'shutdown-fault',
      'channel-fault',
      'observation-fault',
      'observer-fault',
      'spawn-fault',
    ].includes(receipt.fault as string | null),
  );
  assert.equal(receipt.directChildClosed, true);
  assert.equal(receipt.groupAbsent, true);
  return receipt;
}
function stopOutcome(value: unknown, fatal: boolean) {
  const result = exact(value, ['forced', 'ownerCode', 'ownerSignal']);
  assert.equal(result.forced, fatal);
  if (fatal) assert.deepEqual(result, { forced: true, ownerCode: null, ownerSignal: 'SIGKILL' });
  else {
    assert.ok(result.ownerCode === 0 || result.ownerCode === 1);
    assert.equal(result.ownerSignal, null);
  }
  return result;
}
function prehelloOutcome(value: unknown, fatal: boolean) {
  assert.deepEqual(exact(value, ['code', 'signal', 'missingAuthenticatedFamily']), {
    code: fatal ? null : 1,
    signal: fatal ? 'SIGKILL' : null,
    missingAuthenticatedFamily: true,
  });
}
function recovery(value: unknown) {
  const result = exact(value, [
    'changedRecordRefused',
    'missingRecordRefused',
    'exactReconciled',
    'recordHashes',
  ]);
  for (const flag of ['changedRecordRefused', 'missingRecordRefused', 'exactReconciled'])
    assert.equal(result[flag], true);
  assert.ok(Array.isArray(result.recordHashes) && result.recordHashes.length === 3);
  for (const digest of result.recordHashes) hash(digest);
}
function ordered(events: Record<string, unknown>[], required: readonly string[]) {
  let previous = -1;
  for (const name of required) {
    const selected = events.filter((event) => event.event === name);
    assert.equal(selected.length, 1, 'one actual ' + name);
    const index = events.indexOf(selected[0]!);
    assert.ok(index > previous, 'actual event order: ' + name);
    previous = index;
  }
}

/** Finite receipts are observations; no PID or receipt becomes cleanup authority here. */
function validateFrameworkOwnershipGroupCore(value: unknown, group: Group) {
  const receipt = exact(value, [
    'group',
    'cases',
    'disposition',
    'launchOutcomes',
    'sourceInputSha256',
  ]);
  assert.equal(receipt.group, group);
  assert.equal(receipt.disposition, group === 'startup' ? 'retained-missing-family' : 'removed');
  hash(receipt.sourceInputSha256);
  assert.ok(Array.isArray(receipt.cases));
  assert.equal(receipt.cases.length, expectedCases[group].length);
  receipt.cases.forEach((unknownCase: unknown, index: number) => {
    const [scenario, mode] = expectedCases[group][index]!;
    const extra = scenario.startsWith('ready-')
      ? ['staleCaptureRefused']
      : scenario.startsWith('prehello-')
        ? ['cleanupRefused', 'retained', 'receiptIsObservationOnly']
        : scenario.startsWith('worker-')
          ? [
              'workerIdentity',
              'actualLateWorkerWrite',
              'observationUncertaintyRefused',
              ...(scenario === 'worker-owner-death' ? ['recovery'] : []),
            ]
          : scenario === 'prepare-owner-death'
            ? ['changedRecordRefused', 'missingRecordRefused', 'exactReconciled', 'recordHashes']
            : [];
    const row = exact(unknownCase, [
      'case',
      'mode',
      'outcome',
      'productionManifest',
      'ownerReceipt',
      'events',
      'capture',
      ...extra,
    ]);
    assert.equal(row.case, scenario);
    assert.equal(row.mode, mode);
    const fatal = scenario.endsWith('owner-death');
    if (scenario.startsWith('prehello-')) prehelloOutcome(row.outcome, fatal);
    else {
      const outcome = stopOutcome(row.outcome, fatal);
      if (!fatal && scenario !== 'worker-ordered-stop')
        assert.equal(
          outcome.ownerCode,
          scenario.startsWith('ready-') || scenario === 'worker-positive' ? 0 : 1,
        );
    }
    const manifest = exact(row.productionManifest, ['sha256', 'inputs', 'outputs', 'node', 'pnpm']);
    hash(manifest.sha256);
    integer(manifest.inputs, 1, 100000);
    integer(manifest.outputs, 1, 100000);
    for (const version of [manifest.node, manifest.pnpm])
      assert.ok(typeof version === 'string' && /^\d{1,3}\.\d{1,3}\.\d{1,3}$/.test(version));
    const nativeReceipt = fatal ? null : ownerReceipt(row.ownerReceipt);
    if (fatal) assert.equal(row.ownerReceipt, null);
    if (scenario.startsWith('ready-') || scenario.startsWith('worker-')) {
      if (nativeReceipt) {
        assert.equal(nativeReceipt.listenerReady, true);
        assert.equal(nativeReceipt.startupCancelled, false);
      }
    }
    const capture = exact(row.capture, ['lease', 'producers', 'family']);
    const lease = exact(capture.lease, ['format', 'pid', 'nonce', 'kind']);
    assert.equal(lease.format, 1);
    integer(lease.pid, 2);
    hash(lease.nonce, 32);
    assert.equal(lease.kind, mode);
    assert.ok(Array.isArray(capture.producers) && capture.producers.length <= 3);
    capture.producers.forEach((producer: unknown, ordinal: number) => {
      const family = exact(producer, ['ordinal', 'pid', 'group', 'parentPid', 'leaseNonce']);
      integer(family.pid, 2);
      assert.equal(family.group, family.pid);
      assert.equal(family.ordinal, ordinal + 1);
      assert.equal(family.parentPid, lease.pid);
      assert.equal(family.leaseNonce, lease.nonce);
    });
    const family = scenario.startsWith('prehello-')
      ? null
      : exact(capture.family, ['pid', 'group', 'parentPid', 'leaseNonce', 'incarnation']);
    if (family) {
      integer(family.pid, 2);
      assert.equal(family.group, family.pid);
      assert.equal(family.parentPid, lease.pid);
      assert.equal(family.leaseNonce, lease.nonce);
      hash(family.incarnation, 32);
    } else assert.equal(capture.family, null);
    assert.ok(Array.isArray(row.events) && row.events.length > 0 && row.events.length <= 128);
    const events = row.events.map((event: unknown) => {
      const found = exact(event, ['event', 'case', 'pid', 'parentPid', 'milliseconds']);
      assert.ok(typeof found.event === 'string' && eventNames.includes(found.event));
      assert.equal(found.case, scenario);
      integer(found.pid, 2);
      integer(found.parentPid, 1);
      integer(found.milliseconds, 0, 120000);
      return found;
    });
    if (['prepare-term', 'prepare-int'].includes(scenario)) {
      ordered(events, [
        'prepare-called',
        'prepare-entered',
        'stop-consumed',
        'prepare-released',
        'prepare-settled',
        'next-close-called',
        'next-close-settled',
      ]);
      assert.equal(
        events.some((event) => event.event === 'native-listen'),
        false,
      );
    }
    if (scenario === 'worker-positive')
      ordered(events, [
        'prepare-called',
        'prepare-settled',
        'native-listen',
        'worker-entered',
        'worker-returned',
        'stop-consumed',
        'next-close-called',
        'next-close-settled',
      ]);
    if (scenario.startsWith('worker-')) {
      assert.ok(Array.isArray(row.workerIdentity) && row.workerIdentity.length === 3);
      for (const pid of row.workerIdentity) integer(pid, 2);
      assert.equal(row.workerIdentity[1], family!.pid);
      assert.equal(row.workerIdentity[2], family!.group);
      assert.notEqual(row.workerIdentity[0], family!.pid);
      for (const name of ['worker-entered', 'worker-returned']) {
        const selected = events.filter((event) => event.event === name);
        assert.equal(selected.length, 1);
        assert.equal(selected[0]!.pid, row.workerIdentity[0]);
        if (name === 'worker-entered' || scenario === 'worker-positive')
          assert.equal(selected[0]!.parentPid, family!.pid);
      }
      assert.equal(row.actualLateWorkerWrite, true);
      assert.equal(row.observationUncertaintyRefused, fatal);
      if (fatal) recovery(row.recovery);
    }
    if (scenario === 'prepare-owner-death')
      recovery(
        Object.fromEntries(
          ['changedRecordRefused', 'missingRecordRefused', 'exactReconciled', 'recordHashes'].map(
            (key) => [key, row[key]],
          ),
        ),
      );
    if (scenario.startsWith('ready-')) assert.equal(row.staleCaptureRefused, group === 'recovery');
    if (scenario.startsWith('prehello-')) for (const key of extra) assert.equal(row[key], true);
  });
  assert.ok(Array.isArray(receipt.launchOutcomes));
  const counts: Record<string, number> = {};
  for (const outcome of receipt.launchOutcomes) {
    const launch = exact(outcome, ['disposition', 'result']);
    assert.ok(typeof launch.disposition === 'string');
    counts[launch.disposition] = (counts[launch.disposition] ?? 0) + 1;
    if (launch.disposition === 'retired') stopOutcome(launch.result, false);
    else if (launch.disposition === 'fatal-retired-reconciled') stopOutcome(launch.result, true);
    else if (launch.disposition === 'refused-before-lease')
      assert.deepEqual(launch.result, { code: 1, signal: null, leaseAcquired: false });
    else {
      assert.equal(launch.disposition, 'retained-prehello-missing-family');
      prehelloOutcome(launch.result, object(launch.result).signal === 'SIGKILL');
    }
  }
  assert.deepEqual(
    counts,
    group === 'startup'
      ? { retired: 12, 'retained-prehello-missing-family': 5 }
      : group === 'workers'
        ? { retired: 5, 'refused-before-lease': 8 }
        : { retired: 4, 'fatal-retired-reconciled': 3, 'refused-before-lease': 2 },
  );
  return receipt;
}

function runFrameworkOwnershipGroupCore(group: Group) {
  const parent = mkdtempSync(join(tmpdir(), 'learnstack-framework-guard-' + group + '-'));
  const app = fileURLToPath(new URL('../../', import.meta.url));
  const script = join(app, 'scripts/verify-framework-runtime-ownership.mjs');
  try {
    const result = spawnSync(process.execPath, [script, group], {
      encoding: 'utf8',
      timeout: 150_000,
      maxBuffer: 65536,
      env: {
        PATH: process.env.PATH,
        NODE_ENV: 'production',
        TMPDIR: parent,
        TMP: parent,
        TEMP: parent,
        NODE_TLS_REJECT_UNAUTHORIZED: '1',
        NEXT_TELEMETRY_DISABLED: '1',
      },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    assert.ok(result.error === undefined, 'actual ownership driver completed within bound');
    assert.equal(result.signal, null);
    assert.equal(result.status, 0, 'actual ownership group succeeded');
    assert.equal(result.stderr.length, 0, 'actual ownership group emitted no failure diagnostics');
    const receipt = validateFrameworkOwnershipGroup(JSON.parse(result.stdout), group);
    const remaining = readdirSync(parent);
    if (group === 'startup') {
      assert.equal(remaining.length, 1);
      assert.match(remaining[0]!, /^learnstack-framework-ownership-[A-Za-z0-9]+$/);
      const root = join(parent, remaining[0]!);
      const entry = lstatSync(root);
      assert.ok(entry.isDirectory() && !entry.isSymbolicLink());
      assert.equal(entry.mode & 0o777, 0o700);
      const { sourceInputSha256: _inputDigest, ...retained } = receipt;
      validateRetainedOwnershipEvidence(
        readFileSync(join(root, 'ownership-evidence.json'), 'utf8'),
        retained,
      );
    } else assert.deepEqual(remaining, []);
  } finally {
    // A failed or missing-family scope remains private evidence, never cleanup authority.
    if (readdirSync(parent).length === 0) rmdirSync(parent);
  }
}

export function validateFrameworkOwnershipGroup(value: unknown, group: Group) {
  return closedFrameworkProofSync(() => validateFrameworkOwnershipGroupCore(value, group));
}

/** The input is observational evidence, never an ownership capability. */
export function validateRetainedOwnershipEvidence(raw: string, retained: unknown) {
  return closedFrameworkProofSync(() => assert.deepEqual(JSON.parse(raw), retained));
}

export function runFrameworkOwnershipGroup(group: Group) {
  return closedFrameworkProofSync(() => runFrameworkOwnershipGroupCore(group));
}
