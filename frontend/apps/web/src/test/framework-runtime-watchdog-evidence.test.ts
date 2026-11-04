// @vitest-environment node
// Synthetic receipt controls; no record or PID here supplies process/cleanup authority.
import assert from 'node:assert/strict';

import { it } from 'vitest';

import { validateWorkerWatchdogEvidence } from './framework-runtime-ownership';

function evidence() {
  const family = {
    pid: 2002,
    group: 2002,
    parentPid: 2001,
    leaseNonce: 'a'.repeat(32),
    incarnation: 'b'.repeat(32),
  };
  const hashes = ['c', 'd', 'e'].map((value) => value.repeat(64));
  const names = [
    'worker-entered',
    'render-work-entered',
    'stop-consumed',
    'admission-stop',
    'requests-stop',
    'sessions-stop',
    'watchdog',
    'worker-returned',
  ];
  return {
    family,
    workerIdentity: [2003, 2002, 2002],
    ownerReceipt: {
      code: 1,
      signal: null,
      fault: 'channel-fault',
      handshakeValidated: true,
      listenerReady: true,
      requestedStop: true,
      startupCancelled: false,
      parentLost: false,
      directChildClosed: true,
      groupAbsent: true,
    },
    outcome: { forced: false, ownerCode: 1, ownerSignal: null },
    events: names.map((event, index) => ({
      event,
      case: 'worker-ordered-stop',
      pid: event.startsWith('worker-') ? 2003 : 2002,
      parentPid: event === 'worker-entered' ? 2002 : event === 'worker-returned' ? 1 : 2001,
      milliseconds: event === 'watchdog' ? 5100 : 100 + index,
    })),
    watchdogRetention: {
      watchdog: {
        version: 1,
        pid: 2002,
        parentPid: 2001,
        requestStop: 'pending',
        sample: [1, 0, 0, 1, 0],
        milliseconds: 5100,
      },
      directExit: { version: 1, pid: 2002, parentPid: 2001, code: 1, milliseconds: 5101 },
      beforeHashes: hashes,
      afterWatchdogHashes: [...hashes],
      afterCompetitorHashes: [...hashes],
      ownerLive: true,
      runtimeAbsent: true,
      workerLive: true,
      groupLive: true,
      competingModes: ['production', 'development'],
    },
  };
}
it('accepts the finite combined witness only as an observation', () =>
  assert.doesNotThrow(() => validateWorkerWatchdogEvidence(evidence())));
const dirty: readonly (readonly [string, (value: ReturnType<typeof evidence>) => void])[] = [
  [
    'omitted watchdog',
    (value) => {
      value.events = value.events.filter((event) => event.event !== 'watchdog');
    },
  ],
  [
    'unregistered framework work',
    (value) => {
      value.watchdogRetention.watchdog.sample[3] = 0;
    },
  ],
  [
    'snapshot substituted for actual work',
    (value) => {
      value.watchdogRetention.watchdog.sample = [1, 0, 0, 0, 1];
    },
  ],
  [
    'native zero exit',
    (value) => {
      value.watchdogRetention.directExit.code = 0;
    },
  ],
  [
    'final child zero',
    (value) => {
      value.ownerReceipt.code = 0;
    },
  ],
  [
    'owner zero outcome',
    (value) => {
      value.outcome.ownerCode = 0;
    },
  ],
  [
    'foreign runtime observation',
    (value) => {
      value.watchdogRetention.watchdog.pid = 2003;
    },
  ],
  [
    'worker replaced by runtime',
    (value) => {
      value.workerIdentity[0] = 2002;
    },
  ],
  [
    'changed retained record',
    (value) => {
      value.watchdogRetention.afterCompetitorHashes[1] = 'f'.repeat(64);
    },
  ],
  [
    'unproved group retention',
    (value) => {
      value.watchdogRetention.groupLive = false;
    },
  ],
  [
    'missing competing mode',
    (value) => {
      value.watchdogRetention.competingModes = ['production'];
    },
  ],
  [
    'already settled request stop',
    (value) => {
      value.watchdogRetention.watchdog.requestStop = 'fulfilled';
    },
  ],
  [
    'early watchdog substitution',
    (value) => {
      value.watchdogRetention.watchdog.milliseconds = 1000;
    },
  ],
  [
    'completed render substituted for hold',
    (value) => {
      value.events.splice(2, 0, {
        event: 'render-work-returned',
        case: 'worker-ordered-stop',
        pid: 2002,
        parentPid: 2001,
        milliseconds: 101,
      });
    },
  ],
];
for (const [name, mutate] of dirty)
  it('rejects ' + name, () => {
    const value = evidence();
    mutate(value);
    assert.throws(() => validateWorkerWatchdogEvidence(value), {
      message: 'Framework proof refused',
    });
  });
