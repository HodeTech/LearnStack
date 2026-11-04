// Copied-fixture coordinator. Real framework objects remain private to this process.
import assert from 'node:assert/strict';
import {
  closeSync,
  constants,
  fstatSync,
  lstatSync,
  openSync,
  realpathSync,
  writeSync,
} from 'node:fs';
import { join, resolve } from 'node:path';
import zlib from 'node:zlib';

import {
  createRouterFaultGate,
  faultCases,
  finalCase,
  requireRegisteredOwner,
} from './framework-router-fault-gate.mjs';

const KEY = Symbol.for('learnstack.routerFaultObserver');
const ROUTE = Symbol.for('learnstack.routerFaultRoute');
const CAPTURE = Symbol.for('learnstack.routerFaultAggregateCapture');
const OWNER = Symbol.for('learnstack.next.responseCompressionOwner');
const refuse = () => {
  throw new Error('Copied router fault observation refused');
};
const scalar = (value) =>
  value === null ||
  typeof value === 'boolean' ||
  ['fulfilled', 'rejected', 'pipe', 'router', 'abort', 'failure', 'ended', 'unended'].includes(
    value,
  );

export function createRouterFaultObserver(framework, onFailure) {
  const identity = framework?.identity;
  if (!Object.isFrozen(framework) || !Object.isFrozen(identity) || framework.mode !== 'production')
    refuse();
  const root = resolve(identity.appRoot, '../../..');
  const stat = lstatSync(root);
  if (
    realpathSync(root) !== root ||
    !stat.isDirectory() ||
    stat.isSymbolicLink() ||
    stat.uid !== process.getuid() ||
    (stat.mode & 0o777) !== 0o700 ||
    identity.appRoot !== join(root, 'frontend/apps/web')
  )
    refuse();
  const fd = openSync(
    join(root, '.router-fault.events'),
    constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | constants.O_NOFOLLOW,
    0o600,
  );
  const opened = fstatSync(fd),
    named = lstatSync(root);
  if (
    !opened.isFile() ||
    opened.nlink !== 1 ||
    opened.uid !== process.getuid() ||
    (opened.mode & 0o777) !== 0o600 ||
    named.dev !== stat.dev ||
    named.ino !== stat.ino ||
    realpathSync(root) !== root
  ) {
    closeSync(fd);
    refuse();
  }
  const gate = createRouterFaultGate();
  const gzip = Object.getOwnPropertyDescriptor(zlib, 'createGzip');
  assert.ok(gzip && typeof gzip.value === 'function' && gzip.configurable);
  let selected,
    sequence = 0,
    failed = false,
    closed = false,
    completed = 0;
  const globalKeys = [KEY, ROUTE, CAPTURE];
  const kinds = new Set([
    'arm',
    'route',
    'pull',
    'pipe',
    'allocation',
    'source-fault',
    'encoder-fault',
    'end-bound',
    'end-reject',
    'aggregate-bound',
    'aggregate-rejected',
    'transition',
    'join',
    'result',
    'observer-joined',
    'cleanup-fault',
    'constructor-fault',
    'response-finish',
    'response-close',
    'encoder-close',
    'handler',
    'missing-refused',
    'duplicate-refused',
    'late-refused',
    'retired',
    'unconfirmed',
    'stop',
    'terminal-qualified',
    'watchdog',
    'fault',
  ]);
  function fault() {
    if (!failed) {
      failed = true;
      event('fault');
    }
    onFailure();
  }
  function event(kind, value = null) {
    if (
      closed ||
      !selected ||
      !kinds.has(kind) ||
      ++sequence > 256 ||
      (!scalar(value) && !['join', 'result'].includes(kind))
    ) {
      failed = true;
      onFailure();
      return;
    }
    const line =
      JSON.stringify({ v: 1, p: process.pid, n: sequence, c: selected.name, k: kind, x: value }) +
      '\n';
    try {
      if (Buffer.byteLength(line) > 768 || writeSync(fd, line) !== Buffer.byteLength(line))
        throw new Error();
    } catch {
      failed = true;
      onFailure();
    }
  }
  const current = (response) => {
    if (!selected || selected.response !== response) refuse();
    return selected;
  };
  function guarded(work) {
    try {
      return work();
    } catch {
      fault();
      return undefined;
    }
  }
  const sameOwner = (response) => {
    const state = current(response),
      owner = requireRegisteredOwner(response);
    if (
      owner !== state.owner ||
      Object.getOwnPropertyDescriptor(response, OWNER)?.configurable !== false
    )
      refuse();
    return owner;
  };
  function rejectLateAllocation(state) {
    let called = false,
      refused = false;
    try {
      state.owner.createCompressor(() => {
        called = true;
        return undefined;
      });
    } catch {
      refused = true;
    }
    assert.equal(refused, true);
    assert.equal(called, false);
  }
  function registration(state) {
    // Omit only the qualification witness; the immutable actual owner remains installed.
    const requireWitness = (witness) => {
      assert.equal(witness, requireRegisteredOwner(state.response));
    };
    assert.throws(() => requireWitness(undefined));
    requireWitness(state.owner);
    event('missing-refused');
    const before = ['finish', 'close', 'drain'].map((name) => state.response.rawListeners(name));
    const descriptor = Object.getOwnPropertyDescriptor(state.response, OWNER);
    let nextCalled = false;
    assert.throws(
      () =>
        state.middleware(state.request, state.response, () => {
          nextCalled = true;
        }),
      { name: 'Error', message: 'Duplicate response compression owner' },
    );
    assert.equal(nextCalled, false);
    assert.deepEqual(Object.getOwnPropertyDescriptor(state.response, OWNER), descriptor);
    for (const [i, name] of ['finish', 'close', 'drain'].entries())
      assert.deepEqual(state.response.rawListeners(name), before[i]);
    assert.equal(state.stream, undefined);
    event('duplicate-refused');
  }
  function checkUnconfirmed() {
    if (
      !selected ||
      selected.name !== finalCase ||
      selected.unconfirmed ||
      !selected.responseClosed ||
      !selected.joins.has('pipe')
    )
      return;
    const state = selected;
    gate.unconfirmed(state.response, state.owner.failureState(), !state.ownerSettled);
    assert.equal(state.handlerSettled, false);
    assert.equal(state.stream, undefined);
    rejectLateAllocation(state);
    state.unconfirmed = true;
    event('unconfirmed');
  }
  function maybeRetire() {
    if (!selected || selected.name === finalCase) {
      checkUnconfirmed();
      return;
    }
    const state = selected;
    if (
      !state.responseClosed ||
      !state.handlerSettled ||
      !state.ownerSettled ||
      !state.joins.has('pipe') ||
      !state.joins.has('router') ||
      !state.results.has('pipe') ||
      !state.results.has('router') ||
      (state.stream && !state.encoderClosed)
    )
      return;
    gate.retire(state.response, state.owner.failureState(), true);
    assert.equal(state.response.listeners('drain').includes(state.foreignDrain), true);
    if (state.drainListener)
      assert.equal(state.response.listeners('drain').includes(state.drainListener), false);
    if (state.name === 'end-work-reject-gzip') {
      assert.equal(state.pipeEnd, state.aggregate);
      assert.equal(state.aggregateRejected, true);
      assert.notEqual(state.routePromise, state.aggregate);
    }
    if (state.name === 'finish-cleanup-fault-gzip') {
      const before = state.response.rawListeners('drain');
      assert.throws(() => state.owner.setResponseDrainListener(() => {}));
      assert.deepEqual(state.response.rawListeners('drain'), before);
      event('late-refused');
    }
    state.response.removeListener('drain', state.foreignDrain);
    if (state.response.removeListener !== state.removeListener)
      state.response.removeListener = state.removeListener;
    assert.equal(zlib.createGzip, gzip.value);
    assert.equal(Object.hasOwn(globalThis, ROUTE), false);
    assert.equal(Object.hasOwn(globalThis, CAPTURE), false);
    rejectLateAllocation(state);
    event('retired');
    state.retired = true;
    completed++;
    selected = undefined;
  }
  const api = Object.freeze({
    arm(name, response) {
      assert.equal(failed, false);
      assert.equal(closed, false);
      assert.equal(selected, undefined);
      assert.equal(name, faultCases[completed]);
      const handle = gate.arm(name, response),
        route = gate.routeAdapter(response);
      selected = {
        name,
        response,
        handle,
        joins: new Set(),
        results: new Set(),
        handlerSettled: false,
        ownerSettled: false,
        responseClosed: false,
        finish: false,
        encoderClosed: false,
        removeListener: response.removeListener,
        foreignDrain: () => {},
        retired: false,
      };
      const state = selected;
      response.on('drain', state.foreignDrain);
      for (const key of [ROUTE, CAPTURE]) assert.equal(Object.hasOwn(globalThis, key), false);
      Object.defineProperty(globalThis, ROUTE, {
        configurable: true,
        value: Object.freeze({
          begin() {
            assert.equal(selected, state);
            const value = route.begin();
            delete globalThis[ROUTE];
            event('route');
            return Object.freeze({
              ...value,
              notePull() {
                assert.equal(state.pull, undefined);
                state.pull = true;
                event('pull');
              },
              noteSourceFault(error) {
                value.noteSourceFault(error);
                state.sourceFault = true;
                event('source-fault');
              },
              bindEndWork(store, promise) {
                value.bindEndWork(store, promise);
                state.routePromise = promise;
                event('end-bound');
              },
              rejectEndWork(store, error) {
                value.rejectEndWork(store, error);
                event('end-reject');
              },
            });
          },
        }),
      });
      if (name === 'end-work-reject-gzip')
        Object.defineProperty(globalThis, CAPTURE, {
          configurable: true,
          value: Object.freeze({
            capture(promise, response) {
              if (response !== state.response) return;
              assert.equal(state.aggregate, undefined);
              assert.ok(promise && typeof promise.then === 'function');
              state.aggregate = promise;
              event('aggregate-bound');
              delete globalThis[CAPTURE];
              void Promise.resolve(promise).then(
                () => fault(),
                (error) =>
                  guarded(() => {
                    assert.equal(error, state.handle.errors.end);
                    state.aggregateRejected = true;
                    event('aggregate-rejected');
                  }),
              );
            },
          }),
        });
      if (name === 'finish-cleanup-fault-gzip')
        response.removeListener = function (eventName, listener) {
          const result = Reflect.apply(state.removeListener, this, [eventName, listener]);
          if (
            state.cleanupPoint &&
            !state.cleanupInjected &&
            eventName === 'finish' &&
            listener === state.terminalListener
          ) {
            assert.equal(state.finish, true);
            assert.equal(response.writableFinished, true);
            state.cleanupInjected = true;
            response.removeListener = state.removeListener;
            gate.cleanupFault(response, handle.errors.cleanup);
            event('cleanup-fault');
            throw handle.errors.cleanup;
          }
          return result;
        };
      if (
        [
          'constructor-failure-gzip',
          'source-cleanup-fault-gzip',
          'encoder-early-fault-gzip',
        ].includes(name)
      ) {
        assert.equal(zlib.createGzip, gzip.value);
        Object.defineProperty(zlib, 'createGzip', {
          ...gzip,
          value: function (options) {
            let stream;
            try {
              stream = Reflect.apply(gzip.value, zlib, [
                name === finalCase ? { ...(options ?? {}), level: 100 } : options,
              ]);
            } catch (error) {
              if (name !== finalCase) throw error;
              gate.constructorFault(response, error);
              state.constructorError = error;
              event('constructor-fault');
              throw error;
            } finally {
              Object.defineProperty(zlib, 'createGzip', gzip);
            }
            assert.notEqual(name, finalCase, 'Actual constructor must reject');
            if (name === 'source-cleanup-fault-gzip') {
              const destroy = stream.destroy;
              stream.destroy = function (...args) {
                const result = Reflect.apply(destroy, this, args);
                if (state.sourceFault && !state.cleanupInjected) {
                  stream.destroy = destroy;
                  state.cleanupInjected = true;
                  gate.cleanupFault(response, handle.errors.cleanup);
                  event('cleanup-fault');
                  throw handle.errors.cleanup;
                }
                return result;
              };
            } else {
              stream.once('error', (error) =>
                guarded(() => {
                  gate.encoderFault(response, error);
                  state.encoderError = error;
                  event('encoder-fault');
                }),
              );
              queueMicrotask(() =>
                guarded(() => {
                  assert.equal(state.stream, stream);
                  stream.end();
                  stream.write(Buffer.from('x'));
                }),
              );
            }
            return stream;
          },
        });
      }
      event('arm');
    },
    registrationPath(request, response, middleware) {
      const state = current(response);
      assert.equal(state.middleware, undefined);
      assert.equal(typeof middleware, 'function');
      state.request = request;
      state.middleware = middleware;
    },
    ownerInstall(response, owner, terminal) {
      const state = current(response);
      assert.equal(state.owner, undefined);
      assert.ok(
        Object.isFrozen(owner) &&
          typeof state.middleware === 'function' &&
          typeof terminal === 'function',
      );
      state.owner = owner;
      state.terminalListener = terminal;
    },
    pipe(response, endWork) {
      const state = current(response),
        owner = sameOwner(response);
      assert.equal(state.pipeSeen, undefined);
      state.pipeSeen = true;
      state.pipeEnd = endWork;
      if (['pipe-before-selection', 'abort-before-selection'].includes(state.name))
        assert.equal(response.headersSent, false);
      if (state.name === 'end-work-reject-gzip') assert.equal(endWork, state.aggregate);
      const promise = owner.join();
      assert.equal(promise, owner.join());
      state.ownerPromise = promise;
      void promise.then(
        () =>
          guarded(() => {
            state.ownerSettled = true;
            event('observer-joined', 'fulfilled');
            maybeRetire();
          }),
        () =>
          guarded(() => {
            state.ownerSettled = true;
            event('observer-joined', 'rejected');
            maybeRetire();
          }),
      );
      if (state.name === 'finish-cleanup-fault-gzip') registration(state);
      gate.pipe(response);
      event('pipe');
    },
    allocation(response, stream) {
      const state = current(response);
      assert.equal(state.stream, undefined);
      assert.equal(stream.constructor.name, 'Gzip');
      state.stream = stream;
      gate.allocation(response);
      event('allocation');
    },
    drain(response, listener) {
      const state = current(response);
      assert.equal(state.drainListener, undefined);
      state.drainListener = listener;
    },
    cleanup(response, terminal) {
      const state = current(response);
      assert.equal(terminal, state.terminalListener);
      if (state.name === 'finish-cleanup-fault-gzip' && state.finish) state.cleanupPoint = true;
    },
    transition(response, error, abort) {
      const state = current(response),
        owner = sameOwner(response);
      assert.equal(owner.allocationClosed(), true);
      if (state.name === 'abort-before-selection') {
        assert.equal(abort, true);
        assert.equal(error?.name, 'ResponseAborted');
      }
      if (state.sourceFault)
        assert.equal(owner.failureState().operationFailure, state.handle.errors.primary);
      if (!state.transitionSeen) {
        state.transitionSeen = true;
        rejectLateAllocation(state);
        event('transition', abort ? 'abort' : 'failure');
      }
    },
    join(response, owner, endWork, attestation) {
      const state = current(response);
      assert.equal(owner, sameOwner(response));
      assert.equal(state.joins.has(attestation.phase), false);
      assert.equal(owner.allocationClosed(), true);
      assert.equal(owner.join(), state.ownerPromise);
      if (state.name === 'end-work-reject-gzip' && attestation.phase === 'pipe')
        assert.equal(endWork, state.aggregate);
      state.joins.add(attestation.phase);
      event('join', attestation);
      checkUnconfirmed();
    },
    result(response, phase, hasPrimary, primary, hasEnd, endFailure, hasCleanup, cleanupFailure) {
      const state = current(response);
      assert.equal(state.results.has(phase), false);
      assert.equal(state.joins.has(phase), true);
      for (const value of [hasPrimary, hasEnd, hasCleanup]) assert.equal(typeof value, 'boolean');
      if (phase === 'pipe') {
        if (state.sourceFault) {
          assert.equal(hasPrimary, true);
          assert.equal(primary?.cause, state.handle.errors.primary);
        }
        if (state.name === 'end-work-reject-gzip') {
          assert.equal(hasEnd, true);
          assert.equal(endFailure, state.handle.errors.end);
        }
        if (state.cleanupInjected) {
          assert.equal(hasCleanup, true);
          assert.equal(cleanupFailure, state.handle.errors.cleanup);
        }
        if (state.encoderError) {
          assert.equal(hasCleanup, true);
          assert.equal(cleanupFailure, state.encoderError);
        }
      }
      state.results.add(phase);
      event('result', { phase, hasPrimary, hasEnd, hasCleanup });
    },
    finish(response) {
      const state = current(response);
      gate.responseFinish(response);
      state.finish = true;
      event('response-finish');
    },
    responseClose(response) {
      const state = current(response);
      gate.responseClose(response);
      state.responseClosed = true;
      if (state.name === 'abort-before-selection') gate.peerClose(response);
      event('response-close');
      guarded(maybeRetire);
    },
    encoderClose(response, stream) {
      const state = current(response);
      assert.equal(stream, state.stream);
      assert.equal(stream.closed, true);
      assert.equal(state.encoderClosed, false);
      state.encoderClosed = true;
      event('encoder-close', stream.writableEnded ? 'ended' : 'unended');
      guarded(maybeRetire);
    },
    handler(response, failedHandler) {
      const state = current(response);
      gate.handlerSettled(response);
      state.handlerSettled = true;
      event('handler', failedHandler ? 'rejected' : 'fulfilled');
      guarded(maybeRetire);
    },
    ready(response) {
      return !selected || selected.response !== response;
    },
    stop() {
      if (selected && selected.stopAt === undefined) {
        selected.stopAt = performance.now();
        event('stop');
      }
    },
    watchdog(qualified) {
      assert.equal(failed, false);
      assert.equal(qualified, true);
      assert.ok(
        selected?.name === finalCase &&
          selected.unconfirmed &&
          !selected.ownerSettled &&
          !selected.handlerSettled,
      );
      assert.ok(performance.now() - selected.stopAt >= 4500);
      event('terminal-qualified');
      event('watchdog');
    },
    fail: fault,
    failed: () => failed,
    close() {
      if (closed) return;
      closed = true;
      process.removeListener('unhandledRejection', unhandled);
      process.removeListener('uncaughtExceptionMonitor', unhandled);
      for (const key of globalKeys) delete globalThis[key];
      closeSync(fd);
      assert.equal(selected, undefined);
      assert.equal(completed, 7);
      assert.equal(failed, false);
    },
  });
  const unhandled = () => fault();
  process.on('unhandledRejection', unhandled);
  process.on('uncaughtExceptionMonitor', unhandled);
  assert.equal(Object.hasOwn(globalThis, KEY), false);
  Object.defineProperty(globalThis, KEY, { configurable: true, value: api });
  return api;
}
