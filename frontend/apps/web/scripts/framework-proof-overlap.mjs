// Copied-fixture overlap coordinator; never imported by the product runtime.
import { constants, closeSync, lstatSync, openSync, realpathSync, writeSync } from 'node:fs';
import { join, resolve } from 'node:path';

const EVENTS = new Set([
  'armed',
  'reserve-b',
  'reserve-a',
  'track-b',
  'track-a',
  'bounded-b-tracked',
  'bounded-b-fulfilled',
  'bounded-b-rejected',
  'track-b-fulfilled',
  'track-b-rejected',
  'track-a-fulfilled',
  'track-a-rejected',
  'handler-b-fulfilled',
  'handler-b-rejected',
  'handler-a-fulfilled',
  'handler-a-rejected',
  'frame-b-cleaned',
  'frame-a-cleaned',
  'lease-b-cleaned',
  'lease-a-cleaned',
  'api-entered',
  'api-tracked',
  'api-fulfilled',
  'api-rejected',
  'framework-registered',
  'framework-settled',
  'render-entered',
  'render-settled',
  'snapshot-tracked',
  'snapshot-settled',
  'response-closed',
  'encoder-closed',
  'native-stop-started',
  'native-stop-fulfilled',
  'native-stop-rejected',
  'publish-b',
  'release-render',
  'release-framework',
  'release-snapshot',
  'release-api',
  'snapshot',
]);
const fail = () => {
  throw new Error('Copied overlap proof refused');
};
function deferred() {
  let resolveActual;
  const promise = new Promise((resolvePromise) => {
    resolveActual = resolvePromise;
  });
  let released = false;
  return Object.freeze({
    promise,
    release() {
      if (released) fail();
      released = true;
      resolveActual();
    },
  });
}
/** No production import: caller is the fixed copied runtime after admission. */
export function createCopiedOverlapProof(framework) {
  const app = framework?.identity?.appRoot;
  if (typeof app !== 'string' || framework.mode !== 'production') fail();
  const root = resolve(app, '../../..');
  const rootStat = lstatSync(root);
  if (
    app !== join(root, 'frontend/apps/web') ||
    realpathSync(app) !== app ||
    realpathSync(root) !== root ||
    !rootStat.isDirectory() ||
    rootStat.isSymbolicLink() ||
    rootStat.uid !== process.getuid() ||
    (rootStat.mode & 0o777) !== 0o700
  )
    fail();
  const fd = openSync(
    join(root, '.overlap-proof.events'),
    constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | constants.O_NOFOLLOW,
    0o600,
  );
  let seq = 0,
    armed = false,
    apiClaimed = false,
    apiObserved = false,
    reserved = 0,
    releaseIndex = 0;
  let stageAcknowledged = false,
    bTracked = 0,
    bBounded = false;
  let renderRegistered = false,
    frameworkRegistered = false,
    responseClosed = false,
    encoderClosed = false;
  let stopState = 'unstarted',
    closed = false,
    diagnosticFault = false;
  const observers = new Set();
  const frames = new WeakMap(),
    leases = new WeakMap(),
    tracked = new WeakMap();
  const state = {
    aHandler: 'unknown',
    bHandler: 'unknown',
    aFrame: false,
    bFrame: false,
    aLease: false,
    bLease: false,
    bPublished: false,
    api: 'unstarted',
    framework: 'unstarted',
    render: 'unstarted',
    snapshot: 'unstarted',
  };
  let apiGate, frameworkGate, snapshotGate;
  function emit(event, snapshot = null) {
    if (closed || !EVENTS.has(event) || ++seq > 128) fail();
    const data =
      JSON.stringify({ version: 1, pid: process.pid, sequence: seq, event, state: snapshot }) +
      '\n';
    if (Buffer.byteLength(data) > 512 || writeSync(fd, data) !== Buffer.byteLength(data)) fail();
  }
  function snapshot() {
    if (!armed) fail();
    // A successful status query acknowledges only an actually completed prior stage.
    const ready =
      stopState === 'pending' &&
      (releaseIndex === 0
        ? !state.aFrame &&
          !state.aLease &&
          !state.bLease &&
          !state.bPublished &&
          state.render === 'pending' &&
          state.framework === 'pending' &&
          state.snapshot === 'pending' &&
          state.api === 'pending'
        : releaseIndex === 1
          ? state.render === 'fulfilled' &&
            state.framework === 'pending' &&
            !state.aFrame &&
            !state.aLease &&
            !state.bLease &&
            !state.bPublished &&
            state.snapshot === 'pending' &&
            state.api === 'pending'
          : releaseIndex === 2
            ? state.framework === 'fulfilled' &&
              state.aFrame &&
              !state.aLease &&
              state.snapshot === 'pending' &&
              state.api === 'pending'
            : releaseIndex === 3
              ? state.aLease && !state.bLease && state.api === 'pending'
              : false);
    stageAcknowledged = ready;
    const current = Object.freeze({
      ...state,
      stop: stopState,
      responseClosed,
      encoderClosed,
      reserved,
      releaseIndex,
      diagnosticFault,
      stageAcknowledged,
      bTracked,
      bBounded,
    });
    emit('snapshot', current);
    return current;
  }
  function trackSettlement(promise, yes, no) {
    if (!promise || typeof promise.then !== 'function') fail();
    // Join the observer's own callback work too; it never resolves the actual promise.
    const observed = Promise.prototype.then.call(promise, yes, no);
    observers.add(observed);
    void observed.then(
      () => observers.delete(observed),
      () => {
        diagnosticFault = true;
        observers.delete(observed);
      },
    );
  }
  function releaseAll() {
    // Child stop/parent loss can release fixture-owned gates, never another owner's work.
    if (!armed) return;
    for (const gate of [frameworkGate, snapshotGate, apiGate]) {
      try {
        gate.release();
      } catch {
        /* A gate may already be released. */
      }
    }
  }
  return Object.freeze({
    arm() {
      if (armed) fail();
      armed = true;
      apiGate = deferred();
      frameworkGate = deferred();
      snapshotGate = deferred();
      emit('armed');
    },
    reserve(frame, lease, frameCleaned) {
      if (!armed) return;
      if (!frame || !lease || frames.has(frame) || leases.has(lease) || ++reserved > 2) fail();
      const id = reserved === 1 ? 'b' : 'a';
      frames.set(frame, id);
      leases.set(lease, id);
      tracked.set(lease, new Set());
      emit('reserve-' + id);
      trackSettlement(
        frameCleaned,
        () => {
          state[id + 'Frame'] = true;
          emit('frame-' + id + '-cleaned');
        },
        fail,
      );
      trackSettlement(
        lease.cleaned,
        () => {
          state[id + 'Lease'] = true;
          emit('lease-' + id + '-cleaned');
        },
        fail,
      );
      if (id === 'a') {
        state.snapshot = 'pending';
        lease.track(snapshotGate.promise);
        emit('snapshot-tracked');
        trackSettlement(
          snapshotGate.promise,
          () => {
            state.snapshot = 'fulfilled';
            emit('snapshot-settled');
          },
          fail,
        );
      }
    },
    track(lease, actual) {
      const id = leases.get(lease);
      if (!armed || !id) return;
      const work = tracked.get(lease);
      if (work.has(actual)) fail();
      work.add(actual);
      if (id === 'b') bTracked++;
      emit('track-' + id);
      trackSettlement(
        actual,
        () => {
          work.delete(actual);
          emit('track-' + id + '-fulfilled');
        },
        () => {
          work.delete(actual);
          emit('track-' + id + '-rejected');
        },
      );
    },
    bounded(lease, actual) {
      if (!armed || leases.get(lease) !== 'b') return;
      if (bBounded || !tracked.get(lease).has(actual)) fail();
      bBounded = true;
      emit('bounded-b-tracked');
      trackSettlement(
        actual,
        () => emit('bounded-b-fulfilled'),
        () => emit('bounded-b-rejected'),
      );
    },
    handler(frame, outcome) {
      const id = frames.get(frame);
      if (!armed || !id) return;
      if (!['fulfilled', 'rejected'].includes(outcome) || state[id + 'Handler'] !== 'unknown')
        fail();
      state[id + 'Handler'] = outcome;
      emit('handler-' + id + '-' + outcome);
    },
    publish(lease) {
      if (armed && leases.get(lease) === 'b') {
        state.bPublished = true;
        emit('publish-b');
      }
    },
    claimApi() {
      if (!armed || apiClaimed) return null;
      apiClaimed = true;
      state.api = 'pending';
      emit('api-entered');
      return apiGate.promise;
    },
    observeApi(actual) {
      if (!apiClaimed || apiObserved || state.api !== 'pending') fail();
      apiObserved = true;
      emit('api-tracked');
      trackSettlement(
        actual,
        () => {
          state.api = 'fulfilled';
          emit('api-fulfilled');
        },
        () => {
          state.api = 'rejected';
          emit('api-rejected');
        },
      );
    },
    registerRender(actualRender, frameworkContext) {
      if (
        !armed ||
        renderRegistered ||
        reserved !== 2 ||
        !frameworkContext ||
        typeof frameworkContext.waitUntil !== 'function'
      )
        fail();
      renderRegistered = true;
      state.render = 'pending';
      emit('render-entered');
      trackSettlement(
        actualRender,
        () => {
          state.render = 'fulfilled';
          emit('render-settled');
        },
        fail,
      );
      frameworkRegistered = true;
      state.framework = 'pending';
      frameworkContext.waitUntil(frameworkGate.promise);
      emit('framework-registered');
      trackSettlement(
        frameworkGate.promise,
        () => {
          state.framework = 'fulfilled';
          emit('framework-settled');
        },
        fail,
      );
    },
    responseClose() {
      if (!armed || !renderRegistered || responseClosed) fail();
      responseClosed = true;
      emit('response-closed');
    },
    encoderClose() {
      if (!armed || !renderRegistered || encoderClosed) fail();
      encoderClosed = true;
      emit('encoder-closed');
    },
    startNativeStop(runtime) {
      if (
        !armed ||
        reserved !== 2 ||
        !apiClaimed ||
        !apiObserved ||
        !bTracked ||
        !bBounded ||
        !renderRegistered ||
        !frameworkRegistered ||
        !responseClosed ||
        !encoderClosed ||
        state.aFrame ||
        state.aLease ||
        state.bLease ||
        state.bPublished ||
        stopState !== 'unstarted' ||
        typeof runtime?.stop !== 'function'
      )
        fail();
      stopState = 'pending';
      emit('native-stop-started');
      const actual = runtime.stop();
      trackSettlement(
        actual,
        () => {
          stopState = 'fulfilled';
          emit('native-stop-fulfilled');
        },
        () => {
          stopState = 'rejected';
          emit('native-stop-rejected');
        },
      );
    },
    releaseNext(releaseRender) {
      if (stopState !== 'pending' || !stageAcknowledged || releaseIndex >= 4) fail();
      stageAcknowledged = false;
      const stage = releaseIndex++;
      if (stage === 0) {
        if (typeof releaseRender !== 'function' || releaseRender() !== undefined) fail();
        emit('release-render');
      } else if (stage === 1) {
        frameworkGate.release();
        emit('release-framework');
      } else if (stage === 2) {
        snapshotGate.release();
        emit('release-snapshot');
      } else {
        apiGate.release();
        emit('release-api');
      }
    },
    releaseAll,
    snapshot,
    async close() {
      if (closed) fail();
      try {
        while (observers.size) await Promise.allSettled([...observers]);
        if (diagnosticFault) fail();
      } finally {
        closed = true;
        closeSync(fd);
      }
    },
  });
}
