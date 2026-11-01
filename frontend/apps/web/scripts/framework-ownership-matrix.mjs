// Actual startup, initialized worker and exact recovery controls in qualified private source scopes.
import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readdirSync,
  realpathSync,
  renameSync,
  rmSync,
  writeFileSync,
  writeSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { prepareBffProof } from './bff-proof-host.mjs';
import { assertBffPrivateOutput, bffListenerFailureDetail } from './bff-proof-support.mjs';
import { createFixtureOwner, fixtureEnvironment } from './fixture-support.mjs';
import {
  assertNoCanonicalOwnershipReaders,
  createOwnershipMatrixScope,
  readOwnershipObservation,
} from './framework-ownership-matrix-scope.mjs';
import { ownershipMatrixCases } from './framework-ownership-matrix-source.mjs';
import { createQualifiedPrivateFrameworkSource } from './framework-private-source.mjs';
import { FRAMEWORK_FRONTEND_ROOT } from '../../../scripts/framework-installation.mjs';

const groups = ['startup', 'workers', 'recovery'];
const admissionReasons = [
  'unobserved',
  'owner_busy',
  'graph_identity',
  'development_identity',
  'development_compiler',
  'producer_failed',
  'framework-other',
  'runtime-other',
];
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
const pause = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));
const digest = (value) => createHash('sha256').update(value).digest('hex');
const exact = (object, keys) => assert.deepEqual(Object.keys(object).sort(), [...keys].sort());
function validateEvents(found, scenario) {
  assert.ok(Array.isArray(found) && found.length <= 128);
  for (const event of found) {
    exact(event, ['event', 'case', 'pid', 'parentPid', 'milliseconds']);
    assert.ok(eventNames.includes(event.event));
    assert.equal(event.case, scenario);
    assert.ok(Number.isSafeInteger(event.pid) && event.pid > 1);
    assert.ok(Number.isSafeInteger(event.parentPid) && event.parentPid > 0);
    assert.ok(
      Number.isSafeInteger(event.milliseconds) &&
        event.milliseconds >= 0 &&
        event.milliseconds <= 120000,
    );
  }
}
function validateOwnerReceipt(receipt) {
  const booleans = [
    'handshakeValidated',
    'listenerReady',
    'requestedStop',
    'startupCancelled',
    'parentLost',
    'directChildClosed',
    'groupAbsent',
  ];
  exact(receipt, ['code', 'signal', 'fault', ...booleans]);
  for (const key of booleans) assert.equal(typeof receipt[key], 'boolean');
  assert.ok(
    receipt.code === null ||
      (Number.isInteger(receipt.code) && receipt.code >= 0 && receipt.code <= 255),
  );
  assert.ok([null, 'SIGTERM', 'SIGINT', 'SIGKILL'].includes(receipt.signal));
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
    ].includes(receipt.fault),
  );
}
function validateCompletedCase(value) {
  exact(value, ['case', 'mode', 'outcome', 'productionManifest', 'ownerReceipt', 'events']);
  assert.ok(ownershipMatrixCases.includes(value.case));
  assert.ok(['production', 'development'].includes(value.mode));
  validateEvents(value.events, value.case);
  if (value.ownerReceipt !== null) validateOwnerReceipt(value.ownerReceipt);
  const manifest = value.productionManifest;
  exact(manifest, ['sha256', 'inputs', 'outputs', 'node', 'pnpm']);
  assert.match(manifest.sha256, /^[0-9a-f]{64}$/);
  for (const key of ['inputs', 'outputs'])
    assert.ok(Number.isSafeInteger(manifest[key]) && manifest[key] > 0 && manifest[key] <= 100000);
  for (const key of ['node', 'pnpm']) assert.match(manifest[key], /^\d{1,3}\.\d{1,3}\.\d{1,3}$/);
  const outcome = value.outcome;
  if (value.case.startsWith('prehello-')) {
    exact(outcome, ['code', 'signal', 'missingAuthenticatedFamily']);
    assert.equal(outcome.missingAuthenticatedFamily, true);
    assert.ok(
      (outcome.code === 1 && outcome.signal === null) ||
        (outcome.code === null && outcome.signal === 'SIGKILL'),
    );
  } else {
    exact(outcome, ['forced', 'ownerCode', 'ownerSignal']);
    assert.equal(typeof outcome.forced, 'boolean');
    assert.ok(
      (!outcome.forced && [0, 1].includes(outcome.ownerCode) && outcome.ownerSignal === null) ||
        (outcome.forced && outcome.ownerCode === null && outcome.ownerSignal === 'SIGKILL'),
    );
  }
}
function validateFailureEvidence(evidence, group) {
  exact(evidence, ['group', 'stage', 'current', 'completedCases', 'retained', 'cleanupConfirmed']);
  assert.equal(evidence.group, group);
  const stages = [
    'build',
    'cleanup',
    'restart:build',
    ...ownershipMatrixCases.flatMap((name) =>
      ['start', 'stop', 'direct-exit', 'worker-release'].map((phase) => name + ':' + phase),
    ),
  ];
  assert.ok(stages.includes(evidence.stage));
  assert.equal(typeof evidence.retained, 'boolean');
  assert.equal(typeof evidence.cleanupConfirmed, 'boolean');
  assert.ok(Array.isArray(evidence.completedCases) && evidence.completedCases.length <= 17);
  for (const value of evidence.completedCases) validateCompletedCase(value);
  if (evidence.current !== null) {
    exact(evidence.current, ['case', 'mode', 'events', 'ownerReceipt', 'admissionReason']);
    assert.ok(ownershipMatrixCases.includes(evidence.current.case));
    assert.ok(['production', 'development'].includes(evidence.current.mode));
    validateEvents(evidence.current.events, evidence.current.case);
    if (evidence.current.ownerReceipt !== null) validateOwnerReceipt(evidence.current.ownerReceipt);
    assert.ok(
      evidence.current.admissionReason === null ||
        admissionReasons.includes(evidence.current.admissionReason),
    );
  }
}
function absent(pid, group = false) {
  try {
    process.kill(group ? -pid : pid, 0);
    return false;
  } catch (error) {
    if (error.code === 'ESRCH') return true;
    throw error;
  }
}
function capturedLeases(receipt) {
  assert.ok(Array.isArray(receipt.cases));
  assert.equal(receipt.cases.length, { startup: 17, workers: 5, recovery: 7 }[receipt.group]);
  return receipt.cases.map((value) => {
    assert.ok(ownershipMatrixCases.includes(value.case));
    const capture = value.capture;
    exact(capture, ['lease', 'producers', 'family']);
    const lease = capture.lease;
    exact(lease, ['format', 'pid', 'nonce', 'kind']);
    assert.equal(lease.kind, value.mode);
    assert.ok(Array.isArray(capture.producers) && capture.producers.length <= 3);
    for (const [index, producer] of capture.producers.entries()) {
      exact(producer, ['ordinal', 'pid', 'group', 'parentPid', 'leaseNonce']);
      assert.equal(producer.ordinal, index + 1);
      assert.ok(Number.isSafeInteger(producer.pid) && producer.pid > 1);
      assert.equal(producer.group, producer.pid);
      assert.equal(producer.parentPid, lease.pid);
      assert.equal(producer.leaseNonce, lease.nonce);
    }
    if (value.case.startsWith('prehello-')) {
      assert.equal(receipt.group, 'startup');
      assert.equal(receipt.disposition, 'retained-missing-family');
      assert.equal(capture.family, null);
      assert.equal(value.cleanupRefused, true);
      assert.equal(value.retained, true);
    } else {
      exact(capture.family, ['pid', 'group', 'parentPid', 'leaseNonce', 'incarnation']);
      assert.ok(Number.isSafeInteger(capture.family.pid) && capture.family.pid > 1);
      assert.equal(capture.family.group, capture.family.pid);
      assert.equal(capture.family.parentPid, lease.pid);
      assert.equal(capture.family.leaseNonce, lease.nonce);
      assert.match(capture.family.incarnation, /^[0-9a-f]{32}$/);
    }
    return lease;
  });
}

/** The copied executable imports its own real guard and host broker; no module-path override. */
export async function runFrameworkOwnershipMatrix(group, privateEntry = false) {
  assert.ok(groups.includes(group), 'closed ownership matrix');
  if (privateEntry) return runPrivateMatrix(group);
  const root = realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-framework-ownership-')));
  const canonicalFrontend = realpathSync(new URL('../../../', import.meta.url));
  assert.equal(FRAMEWORK_FRONTEND_ROOT, canonicalFrontend);
  const owner = createFixtureOwner();
  owner.install();
  let stage = 'qualification';
  const deadline = setTimeout(() => {
    process.stderr.write('Framework ownership matrix deadline: ' + stage + '\n');
    void owner.cancel();
  }, 120_000);
  deadline.unref();
  try {
    const source = createQualifiedPrivateFrameworkSource({ root });
    writeFileSync(
      join(root, 'ownership-source-inputs.json'),
      JSON.stringify({ inputSha256: source.inputSha256, inventory: source.inventory }) + '\n',
      { flag: 'wx', mode: 0o600 },
    );
    stage = group;
    const child = owner.ownChild(
      spawn(process.execPath, [source.driverPath, group, '--qualified-private'], {
        cwd: root,
        env: fixtureEnvironment({ root, privateValues: {} }),
        detached: true,
        stdio: ['ignore', 'pipe', 'pipe'],
      }),
    );
    let stdout = '',
      stderr = '';
    let overflow = false;
    child.stdout.on('data', (chunk) => {
      stdout += chunk.toString();
      if (stdout.length > 65536) {
        overflow = true;
        child.kill('SIGTERM');
      }
    });
    child.stderr.on('data', (chunk) => {
      stderr += chunk.toString();
      if (stderr.length > 65536) {
        overflow = true;
        child.kill('SIGTERM');
      }
    });
    const closed = await new Promise((resolve, reject) => {
      child.once('close', (code, signal) => resolve({ code, signal }));
      child.once('error', reject);
    });
    assert.equal(overflow, false);
    if (closed.code !== 0 || closed.signal !== null) {
      if (stdout) {
        const evidence = JSON.parse(stdout);
        validateFailureEvidence(evidence, group);
        process.stderr.write(
          'Framework ownership matrix evidence: ' + JSON.stringify(evidence) + '\n',
        );
      }
      // Only finite driver diagnostics leave the inner scope; no copied raw child output.
      for (const line of stderr.trim().split('\n'))
        if (/^Framework ownership matrix (?:failed|deadline): [a-z:-]+$/.test(line))
          process.stderr.write(line + '\n');
      throw new Error('Private ownership matrix failed');
    }
    assert.equal(stderr, '');
    const receipt = JSON.parse(stdout);
    exact(receipt, ['group', 'cases', 'disposition', 'launchOutcomes']);
    assert.equal(receipt.group, group);
    assert.ok(['removed', 'retained-missing-family'].includes(receipt.disposition));
    assert.equal(existsSync(root), receipt.disposition !== 'removed');
    assertNoCanonicalOwnershipReaders(canonicalFrontend, capturedLeases(receipt));
    await owner.dispose();
    clearTimeout(deadline);
    process.stdout.write(
      JSON.stringify({ ...receipt, sourceInputSha256: source.inputSha256 }) + '\n',
    );
  } catch {
    try {
      await owner.dispose();
    } catch {
      /* An uncertain private scope is retained. */
    }
    clearTimeout(deadline);
    process.stderr.write('Framework ownership matrix failed: ' + stage + '\n');
    process.exitCode = 1;
  }
}

async function runPrivateMatrix(group) {
  const root = realpathSync(fileURLToPath(new URL('../../../../../', import.meta.url)));
  assert.equal(realpathSync(process.cwd()), root);
  assert.ok(basename(root).startsWith('learnstack-framework-ownership-'));
  const sourceFrontend = realpathSync(new URL('../../../', import.meta.url));
  assert.equal(sourceFrontend, join(root, 'private-source/frontend'));
  assert.equal(FRAMEWORK_FRONTEND_ROOT, sourceFrontend, 'actual imported private guard identity');
  assert.equal(
    realpathSync(new URL('./framework-proof-host-broker.mjs', import.meta.url)),
    join(sourceFrontend, 'apps/web/scripts/framework-proof-host-broker.mjs'),
    'actual relative broker import belongs to the qualified private graph',
  );
  const scope = createOwnershipMatrixScope(root);
  const pending = [];
  const fixtures = [];
  const results = [];
  let stage = 'build';
  let cancelling = false;
  const own = (promise) => {
    pending.push(promise);
    void promise.catch(() => {});
    return promise;
  };
  function release(fixture, name) {
    const path = join(fixture.controls, name + '.release');
    if (!existsSync(path)) writeFileSync(path, '', { flag: 'wx', mode: 0o600 });
  }
  function releaseCleanup() {
    for (const fixture of fixtures)
      for (const name of ['prepare', 'registration', 'shutdown', 'prehello', 'worker'])
        release(fixture, name);
  }
  async function cancel() {
    if (cancelling) return;
    cancelling = true;
    process.stderr.write('Framework ownership matrix deadline: ' + stage + '\n');
    const evidence = failureEvidence();
    releaseCleanup();
    try {
      const disposed = await scope.dispose();
      evidence.cleanupConfirmed = disposed.disposition === 'removed';
    } catch {
      /* The failed outcome and roots remain retained. */
    }
    evidence.retained = existsSync(root);
    validateFailureEvidence(evidence, group);
    try {
      writeSync(1, JSON.stringify(evidence) + '\n');
    } catch {
      /* A failed diagnostic pipe does not change cancellation. */
    }
    process.exit(1);
  }
  process.once('SIGTERM', cancel);
  process.once('SIGINT', cancel);
  const deadline = setTimeout(cancel, 120_000);
  deadline.unref();
  async function until(predicate) {
    while (!predicate()) {
      assert.equal(cancelling, false);
      await pause(10);
    }
  }
  async function fixture(name) {
    const path = join(root, 'cases', name);
    mkdirSync(path, { recursive: true, mode: 0o700 });
    const controls = join(path, 'ownership-control');
    mkdirSync(controls, { mode: 0o700 });
    writeFileSync(join(controls, 'events.jsonl'), '', { mode: 0o600 });
    const value = { path, controls, prepared: null, current: null, manifest: null };
    fixtures.push(value);
    const secret = randomBytes(32).toString('base64url'),
      marker = randomBytes(32).toString('base64url');
    value.prepared = await prepareBffProof({
      owner: scope.owner,
      root: path,
      secret,
      marker,
      privateValues: [secret, marker],
      ownershipCase: 'matrix',
      isolatedProtocol: true,
    });
    preserveManifest(value);
    return value;
  }
  function preserveManifest(value) {
    const bytes = readOwnershipObservation(
      join(value.path, 'frontend/apps/web/.next/framework-build.json'),
      16 * 1024 * 1024,
    );
    const actual = JSON.parse(bytes);
    assert.equal(actual.mode, 'production');
    assert.equal(actual.producer.node, process.versions.node);
    assert.ok(Array.isArray(actual.inputs) && Array.isArray(actual.outputs));
    const sha256 = digest(bytes);
    const directory = join(root, 'ownership-manifests');
    mkdirSync(directory, { recursive: true, mode: 0o700 });
    const path = join(directory, sha256 + '.json');
    if (existsSync(path)) assert.equal(readOwnershipObservation(path, 16 * 1024 * 1024), bytes);
    else writeFileSync(path, bytes, { flag: 'wx', mode: 0o600 });
    value.manifest = Object.freeze({
      sha256,
      inputs: actual.inputs.length,
      outputs: actual.outputs.length,
      node: actual.producer.node,
      pnpm: actual.producer.pnpm.version,
    });
  }
  function events(value) {
    const text = readOwnershipObservation(join(value.controls, 'events.jsonl'));
    assert.ok(text === '' || text.endsWith('\n'));
    const found =
      text === ''
        ? []
        : text
            .trimEnd()
            .split('\n')
            .map((line) => JSON.parse(line));
    validateEvents(found, value.current.scenario);
    return found;
  }
  const has = (value, name) => events(value).some((event) => event.event === name);
  function ownerReceipt(value) {
    const path = join(value.controls, 'owner-receipt.json');
    if (!existsSync(path)) return null;
    const receipt = JSON.parse(readOwnershipObservation(path, 4096));
    validateOwnerReceipt(receipt);
    return receipt;
  }
  function failureEvidence() {
    const completedCases = results.map(
      ({ case: scenario, mode, outcome, productionManifest, ownerReceipt, events }) => ({
        case: scenario,
        mode,
        outcome,
        productionManifest,
        ownerReceipt,
        events,
      }),
    );
    const evidence = {
      group,
      stage,
      current: null,
      completedCases,
      retained: true,
      cleanupConfirmed: false,
    };
    const current = fixtures.findLast((value) => value.current);
    try {
      if (current)
        evidence.current = {
          case: current.current.scenario,
          mode: current.current.mode,
          events: events(current),
          ownerReceipt: ownerReceipt(current),
          admissionReason: current.current.admissionReason,
        };
    } catch {
      /* Malformed observations are never emitted as evidence. */
    }
    return evidence;
  }
  function launch(value, mode, scenario) {
    assert.ok(ownershipMatrixCases.includes(scenario));
    for (const name of readdirSync(value.controls)) rmSync(join(value.controls, name));
    writeFileSync(join(value.controls, 'events.jsonl'), '', { mode: 0o600 });
    writeFileSync(join(value.controls, 'scenario'), scenario, { mode: 0o600 });
    const running = value.prepared.launch(mode);
    scope.track(running.server);
    own(running.ready);
    const current = { mode, scenario, running, admissionReason: null };
    value.current = current;
    void running.ready.catch((error) => {
      const reason = bffListenerFailureDetail(error)?.frameworkReason;
      if (admissionReasons.includes(reason)) current.admissionReason = reason;
    });
    stage = scenario + ':start';
    return running;
  }
  function records(value) {
    const snapshots = value.current.running.server.broker.records();
    assert.equal(snapshots?.length, 3);
    for (const record of snapshots) {
      const part = relative(root, record.path);
      assert.ok(part && !part.startsWith('..') && !resolve(record.path).startsWith(root + '-'));
      assert.equal(readOwnershipObservation(record.path, 4096), record.bytes);
    }
    return snapshots;
  }
  function unchanged(snapshots) {
    for (const record of snapshots)
      assert.equal(readOwnershipObservation(record.path, 4096), record.bytes);
  }
  function row(value, outcome, extra = {}) {
    const { mode, scenario, running } = value.current;
    const actual = ownerReceipt(value);
    const observations = events(value);
    assertBffPrivateOutput(running.scanners);
    results.push({
      case: scenario,
      mode,
      outcome,
      productionManifest: value.manifest,
      ownerReceipt: actual,
      events: observations,
      capture: {
        lease: running.server.broker.capture.lease(),
        producers: running.server.broker.capture.producers(),
        family: running.server.broker.capture.family(),
      },
      ...extra,
    });
  }
  function requireOrderedEvents(value, required) {
    const observed = events(value);
    const family = value.current.running.server.broker.capture.family();
    let previous = -1;
    for (const name of required) {
      const matches = observed.filter((event) => event.event === name);
      assert.equal(matches.length, 1, 'one actual ' + name + ' event');
      const event = matches[0];
      const index = observed.indexOf(event);
      assert.ok(index > previous, 'actual event order: ' + name);
      previous = index;
      if (!name.startsWith('worker-')) {
        assert.equal(event.pid, family.pid);
        assert.equal(event.parentPid, family.parentPid);
      }
    }
  }
  async function retire(value, expectedCode) {
    const running = value.current.running;
    const result = await scope.retire(running.server, expectedCode);
    assert.ok(
      running.server.broker.records().every((record) => !existsSync(record.path)),
      'ordinary owner released exact records',
    );
    assertBffPrivateOutput(running.scanners);
    return result;
  }
  async function ready(value, mode, scenario, staleBroker) {
    const running = launch(value, mode, scenario);
    await running.ready;
    assert.equal((await own(running.get('/'))).status, 200);
    if (staleBroker) {
      const current = records(value);
      assert.throws(() => staleBroker.reconcile(), /evidence refused/);
      unchanged(current);
    }
    running.server.process.kill(scenario === 'ready-int' ? 'SIGINT' : 'SIGTERM');
    const outcome = await retire(value, 0);
    row(value, outcome, { staleCaptureRefused: Boolean(staleBroker) });
  }
  async function heldPrepare(value, mode, scenario) {
    const running = launch(value, mode, scenario);
    await until(() => has(value, 'prepare-entered'));
    assert.ok(running.server.broker.capture.family());
    assert.equal(has(value, 'prepare-settled'), false);
    stage = scenario + ':stop';
    running.server.process.kill(scenario === 'prepare-int' ? 'SIGINT' : 'SIGTERM');
    await until(() => has(value, 'stop-consumed'));
    if (scenario === 'prepare-watchdog') {
      await pause(2000);
      release(value, 'fault'); // A real second self-signal reaches the already stopped child controller.
    } else release(value, 'prepare');
    const outcome = await retire(value, 1);
    await assert.rejects(running.ready);
    const observed = events(value),
      names = observed.map((event) => event.event);
    assert.equal(names.includes('native-listen'), false);
    if (scenario === 'prepare-watchdog') {
      const elapsed =
        observed.find((event) => event.event === 'watchdog').milliseconds -
        observed.find((event) => event.event === 'stop-consumed').milliseconds;
      assert.ok(elapsed >= 4500 && elapsed < 6500, 'original first-stop watchdog interval');
      assert.equal(names.includes('prepare-settled'), false);
      assert.equal(names.includes('next-close-called'), false);
      assert.ok(names.includes('direct-fault'));
    } else {
      requireOrderedEvents(value, [
        'prepare-called',
        'prepare-entered',
        'stop-consumed',
        'prepare-released',
        'prepare-settled',
        'next-close-called',
        'next-close-settled',
      ]);
    }
    row(value, outcome);
  }
  async function registration(value, scenario) {
    const running = launch(value, 'production', scenario);
    await until(() => has(value, 'registration-entered'));
    running.server.process.kill('SIGTERM');
    await until(() => has(value, 'registration-stop-latched'));
    await pause(2000);
    release(value, 'registration');
    const outcome = await retire(value, 1);
    await assert.rejects(running.ready);
    const observed = events(value),
      names = observed.map((event) => event.event);
    assert.equal(names.includes('prepare-called'), false);
    assert.equal(names.includes('native-listen'), false);
    assert.ok(names.indexOf('registration-released') < names.indexOf('stop-consumed'));
    if (scenario === 'registration-watchdog') {
      const elapsed =
        observed.find((event) => event.event === 'watchdog').milliseconds -
        observed.find((event) => event.event === 'registration-stop-latched').milliseconds;
      assert.ok(
        elapsed >= 4500 && elapsed < 6500,
        'late registration does not reset first-stop watchdog',
      );
    } else assert.equal(names.includes('watchdog'), false);
    row(value, outcome);
  }
  async function prehello(scenario) {
    const value = await fixture(scenario);
    const running = launch(value, 'production', scenario);
    await until(() => has(value, 'prehello-entered'));
    const held = records(value);
    assert.equal(running.server.broker.capture.family(), null);
    const killed = scenario === 'prehello-owner-death';
    if (killed) {
      running.server.process.kill('SIGKILL');
      await until(() => has(value, 'prehello-parent-lost'));
    } else {
      if (!scenario.startsWith('prehello-child-'))
        running.server.process.kill(scenario === 'prehello-int' ? 'SIGINT' : 'SIGTERM');
      await until(() =>
        has(
          value,
          scenario.startsWith('prehello-child-')
            ? 'prehello-signal-observed'
            : 'prehello-stop-observed',
        ),
      );
      release(value, 'prehello');
    }
    const outcome = await scope.retainPreHello(running.server, killed);
    await assert.rejects(running.ready);
    assert.equal(has(value, 'native-imported'), false);
    assert.equal(has(value, 'native-listen'), false);
    const returned = ownerReceipt(value);
    if (killed) {
      assert.equal(returned, null);
      unchanged(held);
    } else {
      assert.ok(
        returned &&
          returned.directChildClosed &&
          returned.groupAbsent &&
          !returned.handshakeValidated &&
          !returned.listenerReady,
      );
      assert.ok(held.every((record) => !existsSync(record.path)));
    }
    row(value, outcome, { cleanupRefused: true, retained: true, receiptIsObservationOnly: true });
  }
  async function competing(value) {
    const held = records(value);
    for (const mode of ['production', 'development']) {
      const other = value.prepared.launch(mode);
      scope.track(other.server);
      own(other.ready);
      await scope.refusedBeforeLease(other.server);
      await assert.rejects(other.ready);
      unchanged(held);
    }
  }
  async function worker(value, scenario) {
    const running = launch(value, 'development', scenario);
    await running.ready;
    assert.equal((await own(running.get('/'))).status, 200);
    let settled = false;
    const request = own(
      running.get('/en/courses/ownership').finally(() => {
        settled = true;
      }),
    );
    await until(() => has(value, 'worker-entered'));
    const entered = events(value).find((event) => event.event === 'worker-entered');
    const family = running.server.broker.capture.family();
    assert.equal(entered.parentPid, family.pid);
    assert.notEqual(entered.pid, family.pid);
    const identity = execFileSync(
      '/bin/ps',
      ['-o', 'pid=,ppid=,pgid=', '-p', String(entered.pid)],
      { encoding: 'utf8', timeout: 5000 },
    )
      .trim()
      .split(/\s+/)
      .map(Number);
    assert.deepEqual(identity, [entered.pid, family.pid, family.group]);
    assert.equal(settled, false);
    const held = records(value);
    if (scenario !== 'worker-positive') {
      stage = scenario + ':direct-exit';
      if (scenario === 'worker-ordered-stop') running.server.process.kill('SIGTERM');
      else if (scenario === 'worker-owner-death') running.server.process.kill('SIGKILL');
      else release(value, 'fault');
      await until(() => absent(family.pid));
      assert.equal(
        absent(entered.pid),
        false,
        'initialized worker survives direct native process exit',
      );
      assert.equal(absent(family.group, true), false);
      assert.equal(has(value, 'worker-returned'), false);
      unchanged(held);
      assert.throws(() => running.server.broker.reconcile(), /evidence refused/);
      if (scenario !== 'worker-owner-death')
        assert.equal(
          running.server.process.exitCode,
          null,
          'live owner holds exact records while descendant remains',
        );
      await competing(value);
    }
    stage = scenario + ':worker-release';
    release(value, 'worker');
    if (scenario === 'worker-positive') {
      const response = await request;
      assert.equal(response.status, 200);
      assert.ok(response.body.includes('Owned static paths worker'));
    } else await Promise.allSettled([request]);
    await until(() => has(value, 'worker-returned') && absent(entered.pid));
    const returned = events(value).filter((event) => event.event === 'worker-returned');
    assert.equal(returned.length, 1);
    assert.equal(returned[0].pid, entered.pid, 'the initialized worker performs the late write');
    let outcome;
    let observationUncertaintyRefused = false;
    if (scenario === 'worker-owner-death') {
      // Closed observer mutant in this private host only: never replace an actual child capability.
      const actualKill = process.kill;
      let uncertainObservations = 0;
      let joining;
      try {
        process.kill = (pid, signal) => {
          if (pid === -family.group && signal === 0) {
            uncertainObservations++;
            throw Object.assign(new Error('Owned observation uncertainty'), { code: 'EPERM' });
          }
          return actualKill(pid, signal);
        };
        joining = own(scope.observeExpectedOwnerDeath(running.server));
        await until(() => uncertainObservations > 0);
        assert.equal(running.server.broker.capture.retired(), false);
        assert.throws(() => running.server.broker.reconcile(), /evidence refused/);
        unchanged(held);
        observationUncertaintyRefused = true;
      } finally {
        process.kill = actualKill;
      }
      outcome = await joining;
    } else {
      if (scenario === 'worker-positive') running.server.process.kill('SIGTERM');
      await running.server.completed;
      const receipt = ownerReceipt(value);
      assert.ok(receipt && receipt.directChildClosed && receipt.groupAbsent);
      if (scenario === 'worker-exit-zero') assert.equal(receipt.code, 0);
      if (scenario === 'worker-exit-one') assert.equal(receipt.code, 1);
      if (scenario === 'worker-self-kill') assert.equal(receipt.signal, 'SIGKILL');
      const expected =
        scenario === 'worker-positive' || (scenario === 'worker-ordered-stop' && receipt.code === 0)
          ? 0
          : 1;
      outcome = await retire(value, expected);
    }
    if (scenario === 'worker-ordered-stop') {
      assert.equal(
        ownerReceipt(value).startupCancelled,
        false,
        'an already ready watchdog failure is not startup cancellation',
      );
      requireOrderedEvents(value, [
        'stop-consumed',
        'admission-stop',
        'requests-stop',
        'sessions-stop',
      ]);
    }
    if (scenario === 'worker-positive')
      requireOrderedEvents(value, [
        'prepare-called',
        'prepare-settled',
        'native-listen',
        'worker-entered',
        'worker-returned',
        'stop-consumed',
        'next-close-called',
        'next-close-settled',
      ]);
    row(value, outcome, {
      workerIdentity: identity,
      actualLateWorkerWrite: true,
      observationUncertaintyRefused,
    });
    return running.server.broker;
  }
  async function recoveryNegatives(value) {
    const server = value.current.running.server;
    const original = records(value);
    const first = original[0];
    writeFileSync(first.path, first.bytes + ' ', { mode: 0o600 });
    assert.throws(() => server.broker.reconcile(), /evidence refused/);
    assert.equal(readOwnershipObservation(first.path, 4096), first.bytes + ' ');
    for (const record of original.slice(1))
      assert.equal(readOwnershipObservation(record.path, 4096), record.bytes);
    writeFileSync(first.path, first.bytes, { mode: 0o600 }); // Restore only this deliberate test-owned byte mutation.
    const backup = first.path + '.owned-negative';
    renameSync(first.path, backup);
    assert.throws(() => server.broker.reconcile(), /evidence refused/);
    assert.equal(existsSync(first.path), false);
    renameSync(backup, first.path);
    unchanged(original);
    scope.reconcileExpectedOwnerDeath(server);
    assert.ok(original.every((record) => !existsSync(record.path)));
    return {
      changedRecordRefused: true,
      missingRecordRefused: true,
      exactReconciled: true,
      recordHashes: original.map((record) => digest(record.bytes)),
    };
  }
  try {
    const value = await fixture('normal');
    if (group === 'startup') {
      await ready(value, 'production', 'ready-term');
      await ready(value, 'production', 'ready-int');
      await heldPrepare(value, 'production', 'prepare-term');
      await heldPrepare(value, 'production', 'prepare-int');
      await heldPrepare(value, 'production', 'prepare-watchdog');
      await registration(value, 'registration-release');
      await registration(value, 'registration-watchdog');
      await ready(value, 'development', 'ready-term');
      await ready(value, 'development', 'ready-int');
      await heldPrepare(value, 'development', 'prepare-term');
      await heldPrepare(value, 'development', 'prepare-int');
      await heldPrepare(value, 'development', 'prepare-watchdog');
      for (const scenario of [
        'prehello-term',
        'prehello-int',
        'prehello-child-term',
        'prehello-child-int',
        'prehello-owner-death',
      ])
        await prehello(scenario);
    } else if (group === 'workers') {
      for (const scenario of [
        'worker-positive',
        'worker-exit-zero',
        'worker-exit-one',
        'worker-self-kill',
        'worker-ordered-stop',
      ])
        await worker(value, scenario);
    } else {
      for (const mode of ['production', 'development'])
        for (const scenario of ['prepare-disconnect', 'prepare-owner-death']) {
          const running = launch(value, mode, scenario);
          await until(() => has(value, 'prepare-entered'));
          const original = records(value);
          if (scenario === 'prepare-disconnect') release(value, 'fault');
          else running.server.process.kill('SIGKILL');
          await until(() => has(value, 'stop-consumed'));
          unchanged(original);
          assert.throws(() => running.server.broker.reconcile(), /evidence refused/);
          release(value, 'prepare');
          if (scenario === 'prepare-disconnect') row(value, await retire(value, 1));
          else {
            const outcome = await scope.observeExpectedOwnerDeath(running.server);
            const negatives = await recoveryNegatives(value);
            row(value, outcome, negatives);
          }
        }
      const staleBroker = await worker(value, 'worker-owner-death');
      const negatives = await recoveryNegatives(value);
      results[results.length - 1].recovery = negatives;
      stage = 'restart:build';
      await value.prepared.rebuild();
      preserveManifest(value);
      await ready(value, 'production', 'ready-term', staleBroker);
      await ready(value, 'development', 'ready-term', staleBroker);
    }
    stage = 'cleanup';
    await Promise.allSettled(pending);
    const disposition = await scope.dispose();
    assert.equal(existsSync(root), disposition.disposition !== 'removed');
    const receipt = { group, cases: results, ...disposition };
    if (disposition.disposition !== 'removed')
      writeFileSync(join(root, 'ownership-evidence.json'), JSON.stringify(receipt) + '\n', {
        flag: 'wx',
        mode: 0o600,
      });
    clearTimeout(deadline);
    process.off('SIGTERM', cancel);
    process.off('SIGINT', cancel);
    process.stdout.write(JSON.stringify(receipt) + '\n');
  } catch {
    const evidence = failureEvidence();
    releaseCleanup();
    try {
      const disposed = await scope.dispose();
      evidence.cleanupConfirmed = disposed.disposition === 'removed';
    } catch {
      /* Keep uncertainty and original failures visible. */
    }
    await Promise.allSettled(pending);
    clearTimeout(deadline);
    evidence.retained = existsSync(root);
    validateFailureEvidence(evidence, group);
    process.stdout.write(JSON.stringify(evidence) + '\n');
    process.stderr.write('Framework ownership matrix failed: ' + stage + '\n');
    process.exitCode = 1;
  }
}
