// Copied-source fixture protocol only. Production owner and child do not import it.
import { AsyncLocalStorage } from 'node:async_hooks';
import { isIP } from 'node:net';

const TAG = 'learnstack-proof-v1';
const PROFILES = new Set(['none', 'ingress', 'admission', 'bff']);
const MODES = new Set([
  'normal',
  'missing',
  'mismatch',
  'duplicate',
  'late',
  'configuration',
  'wrong-status',
]);
const FETCH_STATES = new Set(['pending', 'settled', 'failed']);
const FETCH_ERRORS = new Set([
  'none',
  'connection_refused',
  'timeout',
  'connection_reset',
  'tls',
  'other',
]);
const JOIN_STATES = new Set(['pending', 'fulfilled', 'rejected']);
const JOINS = ['listener', 'next', 'sessions', 'requests'];
// Keep aligned with NativeOperation in src/server/auth/native-operation-contract.ts.
export const NATIVE_OPERATION_KINDS = Object.freeze([
  'listMemberships',
  'listOrganizationMemberships',
  'getOwnContexts',
  'listOrganizations',
  'getMembership',
  'getRole',
  'getOrganization',
  'getOwnProfile',
  'getOwnCapabilities',
  'listRoles',
  'getPermissions',
  'setMembershipProfile',
  'setOwnProfile',
  'replaceMembershipRoles',
  'changeMembershipStatus',
  'changeRoleGrants',
  'adoptRoleManifest',
  'createOrganization',
  'renameOrganization',
  'archiveOrganization',
  'selectDefaultOrganization',
]);
const OPERATION_SET = new Set(NATIVE_OPERATION_KINDS);

const record = (value) => value !== null && typeof value === 'object' && !Array.isArray(value);
const exact = (value, keys) =>
  record(value) && Object.keys(value).sort().join(',') === [...keys].sort().join(',');
const count = (value) => Number.isSafeInteger(value) && value >= 0;
const positive = (value) => count(value) && value > 0;
const port = (value) => Number.isInteger(value) && value >= 1 && value <= 65535;
const fail = () => {
  throw new Error('Copied runtime proof protocol refused');
};

function admissionReply(value) {
  return (
    exact(value, [
      'id',
      'active',
      'snapshots',
      'snapshotsSeen',
      'reads',
      'immutable',
      'lateRefused',
      'lateAccepted',
    ]) &&
    positive(value.id) &&
    ['active', 'snapshots', 'snapshotsSeen', 'reads', 'lateRefused', 'lateAccepted'].every((key) =>
      count(value[key]),
    ) &&
    value.snapshots <= value.active &&
    typeof value.immutable === 'boolean'
  );
}

function ingressCommand(value) {
  return (
    exact(value, ['kind', 'id', 'fence']) &&
    value.kind === 'observe' &&
    positive(value.id) &&
    typeof value.fence === 'string' &&
    /^[0-9a-f]{32}$/.test(value.fence)
  );
}
function admissionCommand(value) {
  return (
    (exact(value, ['id', 'op']) &&
      positive(value.id) &&
      ['observe', 'settled'].includes(value.op)) ||
    (exact(value, ['id', 'op', 'value']) &&
      positive(value.id) &&
      value.op === 'mode' &&
      MODES.has(value.value))
  );
}
function bffCommand(value) {
  return (
    (exact(value, ['kind', 'milliseconds']) &&
      value.kind === 'advance' &&
      [0, 60000, 901000].includes(value.milliseconds)) ||
    (exact(value, ['kind', 'action']) &&
      value.kind === 'render-control' &&
      ['arm', 'release', 'status'].includes(value.action))
  );
}
function bffCommandWire(value) {
  return (
    exact(value, ['kind', 'sequence', 'payload']) &&
    value.kind === 'proof-command' &&
    positive(value.sequence) &&
    bffCommand(value.payload)
  );
}
function bffReplyWire(value) {
  return (
    exact(value, ['kind', 'sequence', 'payload']) &&
    value.kind === 'proof-reply' &&
    positive(value.sequence) &&
    ['snapshot', 'render-state'].includes(value.payload?.kind) &&
    bffObservation(value.payload)
  );
}

function ingressObservation(value, childPid) {
  if (exact(value, ['kind', 'pid', 'port', 'address']) && value.kind === 'bound')
    return (
      value.pid === childPid &&
      port(value.port) &&
      typeof value.address === 'string' &&
      value.address.length <= 64 &&
      isIP(value.address) !== 0
    );
  if (exact(value, ['kind', 'pid', 'phase']) && value.kind === 'readiness')
    return value.pid === childPid && ['preparing', 'prepared'].includes(value.phase);
  if (
    exact(value, ['kind', 'pid', 'state', 'errorKind', 'elapsedMilliseconds']) &&
    value.kind === 'version-fetch'
  )
    return (
      value.pid === childPid &&
      FETCH_STATES.has(value.state) &&
      FETCH_ERRORS.has(value.errorKind) &&
      count(value.elapsedMilliseconds) &&
      value.elapsedMilliseconds <= 120000
    );
  if (exact(value, ['surface', 'sanitized']))
    return ['native', 'sink'].includes(value.surface) && typeof value.sanitized === 'boolean';
  if (exact(value, ['id', 'nextCalls', 'sinkCalls', 'debugDisabled', 'environmentSafe']))
    return (
      positive(value.id) &&
      count(value.nextCalls) &&
      count(value.sinkCalls) &&
      typeof value.debugDisabled === 'boolean' &&
      typeof value.environmentSafe === 'boolean'
    );
  return false;
}

function administrationCounts(value) {
  return (
    record(value) &&
    Object.keys(value).length <= OPERATION_SET.size &&
    Object.entries(value).every(([key, number]) => OPERATION_SET.has(key) && count(number))
  );
}
function snapshotCounts(value) {
  return (
    value === null ||
    (exact(value, ['active', 'owners', 'bytes']) &&
      count(value.active) &&
      count(value.owners) &&
      count(value.bytes) &&
      value.owners <= value.active)
  );
}
function shutdownShape(value) {
  return (
    exact(value, [
      'kind',
      'entered',
      'watchdog',
      'listener',
      'next',
      'sessions',
      'requests',
      'milestone',
      'sample',
    ]) &&
    value.kind === 'proof-shutdown' &&
    value.entered === true &&
    typeof value.watchdog === 'boolean' &&
    JOINS.every((key) => JOIN_STATES.has(value[key])) &&
    typeof value.milestone === 'string' &&
    Array.isArray(value.sample) &&
    value.sample.length === 5 &&
    value.sample.every((number) => Number.isInteger(number) && number >= 0 && number <= 256)
  );
}
function bffObservation(value) {
  if (exact(value, ['kind', 'port']) && value.kind === 'proof-listener') return port(value.port);
  if (
    exact(value, ['kind', 'now', 'refreshes', 'accounts', 'administrations']) &&
    value.kind === 'snapshot'
  )
    return (
      count(value.now) &&
      count(value.refreshes) &&
      count(value.accounts) &&
      administrationCounts(value.administrations)
    );
  if (exact(value, ['kind', 'held', 'frames', 'snapshots']) && value.kind === 'render-state')
    return count(value.held) && count(value.frames) && snapshotCounts(value.snapshots);
  return shutdownShape(value);
}

/** One validator for the relay and the host's existing shutdown observation. */
export function createBffShutdownValidator() {
  let state = Object.freeze({
    kind: 'proof-shutdown',
    entered: false,
    watchdog: false,
    listener: 'pending',
    next: 'pending',
    sessions: 'pending',
    requests: 'pending',
  });
  let frames = 0;
  let sample = 'absent';
  return Object.freeze({
    observe(value) {
      if (!shutdownShape(value) || ++frames > 6) fail();
      const changed = JOINS.filter((key) => value[key] !== state[key]);
      if (frames === 1) {
        if (value.milestone !== 'entered' || value.watchdog || changed.length) fail();
      } else if (
        state.watchdog ||
        (value.watchdog
          ? changed.length !== 0 || value.milestone !== 'watchdog'
          : changed.length !== 1 || value.milestone !== changed[0]) ||
        changed.some((key) => state[key] !== 'pending' || value[key] === 'pending')
      )
        fail();
      state = Object.freeze({
        kind: 'proof-shutdown',
        entered: true,
        watchdog: value.watchdog,
        listener: value.listener,
        next: value.next,
        sessions: value.sessions,
        requests: value.requests,
      });
      sample = ['valid', value.milestone, ...value.sample].join(',');
    },
    snapshot() {
      return Object.freeze({ frames, state, sample });
    },
  });
}

function createState(profile) {
  if (!PROFILES.has(profile)) fail();
  const pending = new Map();
  const usedIds = new Set();
  const bffShutdown = createBffShutdownValidator();
  let shutdownFrames = 0;
  let listenerSeen = false;
  let readinessPhase = 'launched';
  let fetchActive = 0;
  let nextSequence = 1;
  let expectedNow = 0;
  let commandPending = null;
  function command(value) {
    if (profile === 'none') fail();
    if (profile === 'ingress') {
      if (!ingressCommand(value) || usedIds.has(value.id)) fail();
      pending.set(value.id, value.fence);
      usedIds.add(value.id);
    } else if (profile === 'admission') {
      if (!admissionCommand(value) || usedIds.has(value.id)) fail();
      pending.set(value.id, value.op);
      usedIds.add(value.id);
    } else {
      if (!bffCommandWire(value) || value.sequence !== nextSequence || commandPending !== null)
        fail();
      commandPending = Object.freeze({
        sequence: value.sequence,
        kind: value.payload.kind === 'advance' ? 'snapshot' : 'render-state',
      });
      if (value.payload.kind === 'advance') {
        if (!Number.isSafeInteger(expectedNow + value.payload.milliseconds)) fail();
        expectedNow += value.payload.milliseconds;
      }
      nextSequence++;
    }
  }
  function observation(value, childPid) {
    if (profile === 'none') fail();
    if (profile === 'ingress') {
      if (!ingressObservation(value, childPid)) fail();
      if (value.kind === 'readiness') {
        const expected =
          readinessPhase === 'launched'
            ? 'preparing'
            : readinessPhase === 'preparing'
              ? 'prepared'
              : null;
        if (value.phase !== expected) fail();
        readinessPhase = value.phase;
      }
      if (value.kind === 'version-fetch') {
        if (value.state === 'pending') {
          if (value.errorKind !== 'none' || fetchActive === Number.MAX_SAFE_INTEGER) fail();
          fetchActive++;
        } else {
          if (
            fetchActive === 0 ||
            (value.state === 'settled' && value.errorKind !== 'none') ||
            (value.state === 'failed' && value.errorKind === 'none')
          )
            fail();
          fetchActive--;
        }
      }
      if (Object.hasOwn(value, 'id')) {
        if (!pending.has(value.id)) fail();
        pending.delete(value.id);
      }
    } else if (profile === 'admission') {
      if (value?.kind === 'shutdown') {
        if (
          !exact(value, ['kind', 'counts']) ||
          shutdownFrames++ !== 0 ||
          !exact(value.counts, ['active', 'snapshots']) ||
          !count(value.counts.active) ||
          !count(value.counts.snapshots) ||
          value.counts.snapshots > value.counts.active
        )
          fail();
      } else {
        if (!admissionReply(value) || !pending.has(value.id)) fail();
        pending.delete(value.id);
      }
    } else {
      if (value?.kind === 'proof-reply') {
        if (
          !bffReplyWire(value) ||
          !commandPending ||
          value.sequence !== commandPending.sequence ||
          value.payload.kind !== commandPending.kind ||
          (value.payload.kind === 'snapshot' && value.payload.now !== expectedNow)
        )
          fail();
        commandPending = null;
      } else if (value?.kind === 'proof-listener') {
        if (!bffObservation(value)) fail();
        if (listenerSeen) fail();
        listenerSeen = true;
      } else {
        if (!shutdownShape(value)) fail();
        bffShutdown.observe(value);
      }
    }
  }
  return Object.freeze({ command, observation });
}

/** The fixture host independently checks the projected profile before delivery. */
export function createCopiedHostProofValidator(profile) {
  return createState(profile);
}

function envelope(incarnation, payload) {
  return Object.freeze({ tag: TAG, incarnation, payload });
}
function unwrap(frame, incarnation) {
  if (
    !exact(frame, ['tag', 'incarnation', 'payload']) ||
    frame.tag !== TAG ||
    frame.incarnation !== incarnation
  )
    fail();
  return frame.payload;
}

/** Host-to-owner traffic is closed at the owner; only validated child observations escape. */
export function createCopiedParentProof(profile) {
  if (typeof process.send !== 'function' || !process.connected) fail();
  const state = createState(profile);
  let attached = false;
  let lease;
  let runtimeFamily;
  let authenticatedChild;
  const producerPids = new Set();
  let channelFault;
  let hostMessageListener;
  let closing;
  let closed = false;
  let sendFailed = false;
  const pendingSends = new Set();
  const hostDisconnected = () => {
    sendFailed = true;
    process.kill(process.pid, 'SIGTERM');
  };
  process.on('disconnect', hostDisconnected);
  const sendHost = (value) => {
    if (closed || typeof process.send !== 'function' || !process.connected) fail();
    let settle;
    const pending = new Promise((resolve) => {
      settle = resolve;
    });
    pendingSends.add(pending);
    const done = (error) => {
      pendingSends.delete(pending);
      if (error) {
        sendFailed = true;
        if (channelFault) channelFault();
        else process.kill(process.pid, 'SIGTERM');
      }
      settle();
    };
    try {
      process.send(value, done);
    } catch (error) {
      done(error);
      fail();
    }
  };
  return Object.freeze({
    close() {
      if (!closing) {
        closed = true;
        if (hostMessageListener) process.off('message', hostMessageListener);
        closing = (async () => {
          while (pendingSends.size) await Promise.all([...pendingSends]);
          const failed = sendFailed || !process.connected;
          process.off('disconnect', hostDisconnected);
          if (process.connected) process.disconnect();
          if (failed) fail();
        })();
      }
      return closing;
    },
    onLease(snapshot) {
      if (
        lease ||
        !exact(snapshot, ['record', 'records']) ||
        !Array.isArray(snapshot.records) ||
        snapshot.records.length !== 3 ||
        !exact(snapshot.record, ['format', 'pid', 'nonce', 'kind']) ||
        snapshot.record.format !== 1 ||
        snapshot.record.pid !== process.pid ||
        !/^[0-9a-f]{32}$/.test(snapshot.record.nonce) ||
        !['production', 'development'].includes(snapshot.record.kind)
      )
        fail();
      lease = Object.freeze({ ...snapshot.record });
      // Paths and bytes remain local to the trusted owner; the host derives its
      // own qualified record locations from the copied workspace.
      sendHost(Object.freeze({ kind: 'runtime-lease', record: lease }));
    },
    onProducerFamily(handle) {
      if (
        !lease ||
        !exact(handle, ['pid', 'group', 'parentPid']) ||
        !positive(handle.pid) ||
        handle.group !== handle.pid ||
        handle.parentPid !== process.pid ||
        producerPids.has(handle.pid)
      )
        fail();
      producerPids.add(handle.pid);
      sendHost(
        Object.freeze({
          kind: 'producer-family',
          pid: handle.pid,
          group: handle.group,
          parentPid: handle.parentPid,
          leaseNonce: lease.nonce,
        }),
      );
    },
    onRuntimeFamily(handle) {
      if (
        !lease ||
        runtimeFamily ||
        !exact(handle, ['pid', 'group', 'parentPid', 'leaseNonce', 'incarnation']) ||
        !positive(handle.pid) ||
        handle.group !== handle.pid ||
        handle.parentPid !== process.pid ||
        handle.leaseNonce !== lease.nonce ||
        !/^[0-9a-f]{32}$/.test(handle.incarnation) ||
        (authenticatedChild &&
          (authenticatedChild.pid !== handle.pid ||
            authenticatedChild.incarnation !== handle.incarnation))
      )
        fail();
      runtimeFamily = Object.freeze({ ...handle });
      sendHost(Object.freeze({ kind: 'runtime-family', ...runtimeFamily }));
    },
    attach({ send, childPid, incarnation, onFault }) {
      if (
        attached ||
        typeof send !== 'function' ||
        !positive(childPid) ||
        !/^[0-9a-f]{32}$/.test(incarnation) ||
        typeof onFault !== 'function'
      )
        fail();
      attached = true;
      channelFault = onFault;
      authenticatedChild = Object.freeze({ pid: childPid, incarnation });
      const hostMessage = (payload) => {
        try {
          state.command(payload);
          send(envelope(incarnation, payload));
        } catch {
          onFault();
        }
      };
      if (profile !== 'none') {
        hostMessageListener = hostMessage;
        process.on('message', hostMessage);
      }
      return Object.freeze({
        receive(frame) {
          if (
            !runtimeFamily ||
            runtimeFamily.pid !== childPid ||
            runtimeFamily.incarnation !== incarnation
          )
            fail();
          const payload = unwrap(frame, incarnation);
          state.observation(payload, childPid);
          sendHost(payload);
        },
      });
    },
  });
}

/** Child-facing helper holds only profile commands and observations, never control authority. */
export function createCopiedChildProof(profile) {
  const state = createState(profile);
  let attached = false;
  return Object.freeze({
    attach({ send, incarnation, requestStop }) {
      if (
        attached ||
        typeof send !== 'function' ||
        !/^[0-9a-f]{32}$/.test(incarnation) ||
        typeof requestStop !== 'function'
      )
        fail();
      attached = true;
      let handler;
      let watchdogHandler;
      let watchdogFired = false;
      let sealed = false;
      let handlerFailed = false;
      let drainPromise;
      const pending = new Set();
      const commandContext = profile === 'bff' ? new AsyncLocalStorage() : null;
      const handlerFault = () => {
        handlerFailed = true;
        requestStop('protocol-fault');
      };
      return Object.freeze({
        receive(frame) {
          if (sealed) fail();
          const payload = unwrap(frame, incarnation);
          state.command(payload);
          if (!handler) fail();
          try {
            const result = commandContext
              ? commandContext.run(payload.sequence, () => handler(payload.payload))
              : handler(payload);
            if (result && typeof result.then === 'function') {
              let tracked;
              tracked = Promise.resolve(result).then(
                () => {
                  pending.delete(tracked);
                },
                () => {
                  pending.delete(tracked);
                  handlerFault();
                },
              );
              pending.add(tracked);
            }
          } catch {
            handlerFault();
          }
        },
        emit(payload) {
          try {
            const wire =
              profile === 'bff' && ['snapshot', 'render-state'].includes(payload?.kind)
                ? Object.freeze({
                    kind: 'proof-reply',
                    sequence: commandContext.getStore(),
                    payload,
                  })
                : payload;
            state.observation(wire, process.pid);
            send(envelope(incarnation, wire));
          } catch {
            try {
              handlerFault();
            } finally {
              fail();
            }
          }
        },
        onCommand(fn) {
          if (sealed || handler || typeof fn !== 'function') fail();
          handler = fn;
        },
        onWatchdog(fn) {
          if (profile !== 'bff' || sealed || watchdogHandler || typeof fn !== 'function') fail();
          watchdogHandler = fn;
        },
        watchdog() {
          if (profile !== 'bff' || watchdogFired) return;
          watchdogFired = true;
          if (!watchdogHandler) return;
          try {
            if (watchdogHandler() !== undefined) fail();
          } catch {
            handlerFault();
          }
        },
        drain() {
          if (!drainPromise) {
            sealed = true;
            drainPromise = (async () => {
              while (pending.size) await Promise.allSettled([...pending]);
              if (handlerFailed) fail();
            })();
          }
          return drainPromise;
        },
      });
    },
  });
}
