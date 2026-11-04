// Copied-fixture case coordinator. This module has no filesystem or IPC authority.
import assert from 'node:assert/strict';

export const recoverableCases = Object.freeze([
  'pipe-before-selection',
  'abort-before-selection',
  'source-fault-gzip',
  'encoder-early-fault-gzip',
  'source-cleanup-fault-gzip',
  'end-work-reject-gzip',
  'finish-cleanup-fault-gzip',
]);
export const finalCase = 'constructor-failure-gzip';
export const faultCases = Object.freeze([...recoverableCases, finalCase]);
export const registrationCases = Object.freeze([
  'missing-required-owner',
  'duplicate-owner',
  'late-drain-registration',
]);
const caseSet = new Set(faultCases);
const ownerKey = Symbol.for('learnstack.next.responseCompressionOwner');

/** A required compression-owner witness is separate from a valid no-middleware route. */
export function requireRegisteredOwner(response) {
  const owner = response?.[ownerKey];
  assert.ok(owner && Object.isFrozen(owner), 'Required owner registration missing');
  for (const name of ['createCompressor', 'setResponseDrainListener', 'join', 'failureState'])
    assert.equal(typeof owner[name], 'function', 'Required owner capability ' + name);
  return owner;
}

function deferred() {
  let resolve;
  const promise = new Promise((done) => {
    resolve = done;
  });
  return Object.freeze({ promise, resolve });
}

/**
 * One request at a time. The caller must obtain pipe/allocation/peer observations
 * from the same marked native response. None of these methods creates evidence.
 */
export function createRouterFaultGate() {
  let active;
  function current(response) {
    assert.ok(active && active.response === response, 'Exact active native response');
    return active;
  }
  function once(state, field) {
    assert.equal(state[field], false, 'One actual ' + field);
    state[field] = true;
  }
  function arm(name, response) {
    assert.ok(caseSet.has(name), 'Closed router fault case');
    assert.ok(response && typeof response === 'object', 'Native response required');
    assert.equal(active, undefined, 'One active request');
    const state = {
      name,
      response,
      route: false,
      pipe: false,
      allocation: false,
      peerClose: false,
      responseClose: false,
      responseFinish: false,
      handlerSettled: false,
      sourceFault: false,
      endRejected: false,
      cleanupFault: false,
      encoderFault: undefined,
      constructorFault: false,
      constructorError: undefined,
      endWork: undefined,
      pipeGate: deferred(),
      allocationGate: deferred(),
      peerGate: deferred(),
      primary: new Error('Copied source fault'),
      cleanup: new Error('Copied cleanup fault'),
      end: new Error('Copied end work fault'),
    };
    active = state;
    state.handle = Object.freeze({
      name,
      response,
      errors: Object.freeze({ primary: state.primary, cleanup: state.cleanup, end: state.end }),
      pipe: state.pipeGate.promise,
      allocation: state.allocationGate.promise,
      peerClose: state.peerGate.promise,
    });
    return state.handle;
  }
  function route(response) {
    once(current(response), 'route');
  }
  function routeAdapter(response) {
    current(response);
    return Object.freeze({
      begin() {
        const state = current(response);
        route(response);
        return Object.freeze({
          name: state.name,
          pipe: state.handle.pipe,
          allocation: state.handle.allocation,
          peerClose: state.handle.peerClose,
          errors: state.handle.errors,
          noteSourceFault(error) {
            sourceFault(response, error);
          },
          bindEndWork(store, promise) {
            bindEndWork(response, store, promise);
          },
          rejectEndWork(store, error) {
            endRejected(response, error, store);
          },
        });
      },
    });
  }
  function pipe(response) {
    const state = current(response);
    assert.equal(state.route, true, 'Actual router before pipe');
    once(state, 'pipe');
    state.pipeGate.resolve();
  }
  function allocation(response) {
    const state = current(response);
    assert.equal(state.pipe, true, 'Actual pipe before allocation');
    assert.ok(!['pipe-before-selection', 'abort-before-selection'].includes(state.name));
    once(state, 'allocation');
    state.allocationGate.resolve();
  }
  function peerClose(response) {
    const state = current(response);
    assert.equal(state.name, 'abort-before-selection');
    assert.equal(state.pipe, true);
    assert.equal(state.allocation, false);
    once(state, 'peerClose');
    state.peerGate.resolve();
  }
  function sourceFault(response, error) {
    const state = current(response);
    assert.ok(
      ['pipe-before-selection', 'source-fault-gzip', 'source-cleanup-fault-gzip'].includes(
        state.name,
      ),
    );
    assert.equal(state.pipe, true);
    assert.equal(state.allocation, state.name !== 'pipe-before-selection');
    assert.equal(error, state.primary, 'Original source error identity');
    once(state, 'sourceFault');
  }
  function bindEndWork(response, actualWorkStore, promise) {
    const state = current(response);
    assert.equal(state.name, 'end-work-reject-gzip');
    assert.equal(state.endWork, undefined, 'One actual pending end task');
    assert.ok(actualWorkStore && Array.isArray(actualWorkStore.pendingRevalidateWrites));
    assert.ok(actualWorkStore.pendingRevalidateWrites.includes(promise));
    assert.ok(promise && typeof promise.then === 'function');
    state.endWork = Object.freeze({ actualWorkStore, promise });
  }
  function endRejected(response, error, actualWorkStore) {
    const state = current(response);
    assert.equal(state.name, 'end-work-reject-gzip');
    assert.equal(state.allocation, true);
    assert.equal(error, state.end, 'Original end work error identity');
    assert.equal(actualWorkStore, state.endWork?.actualWorkStore);
    assert.ok(actualWorkStore.pendingRevalidateWrites.includes(state.endWork.promise));
    once(state, 'endRejected');
  }
  function responseFinish(response) {
    once(current(response), 'responseFinish');
  }
  function responseClose(response) {
    once(current(response), 'responseClose');
  }
  function cleanupFault(response, error) {
    const state = current(response);
    assert.ok(['source-cleanup-fault-gzip', 'finish-cleanup-fault-gzip'].includes(state.name));
    if (state.name === 'source-cleanup-fault-gzip') assert.equal(state.sourceFault, true);
    else assert.equal(state.responseFinish, true);
    assert.equal(error, state.cleanup, 'Separate cleanup error identity');
    once(state, 'cleanupFault');
  }
  function encoderFault(response, error) {
    const state = current(response);
    assert.equal(state.name, 'encoder-early-fault-gzip');
    assert.equal(state.allocation, true);
    assert.equal(state.encoderFault, undefined, 'One real encoder error');
    assert.ok(error instanceof Error && error.code === 'ERR_STREAM_WRITE_AFTER_END');
    state.encoderFault = error;
  }
  function constructorFault(response, error) {
    const state = current(response);
    assert.equal(state.name, finalCase);
    assert.equal(state.pipe, true);
    assert.equal(state.allocation, false, 'No returned encoder');
    assert.ok(error instanceof Error && error.code === 'ERR_OUT_OF_RANGE');
    once(state, 'constructorFault');
    state.constructorError = error;
  }
  function handlerSettled(response) {
    once(current(response), 'handlerSettled');
  }
  function retire(response, ownerState, joined) {
    const state = current(response);
    assert.notEqual(state.name, finalCase, 'Unconfirmed construction cannot retire cleanly');
    assert.equal(state.pipe, true, 'Actual pipe entry required');
    assert.equal(state.responseClose, true, 'Actual response close required');
    assert.equal(state.handlerSettled, true, 'Actual handler settlement required');
    assert.ok(ownerState && typeof ownerState === 'object');
    const required = {
      'pipe-before-selection': ['sourceFault'],
      'abort-before-selection': ['peerClose'],
      'source-fault-gzip': ['allocation', 'sourceFault'],
      'encoder-early-fault-gzip': ['allocation', 'encoderFault'],
      'source-cleanup-fault-gzip': ['allocation', 'sourceFault', 'cleanupFault'],
      'end-work-reject-gzip': ['allocation', 'endWork', 'endRejected'],
      'finish-cleanup-fault-gzip': ['allocation', 'responseFinish', 'cleanupFault'],
    }[state.name];
    assert.ok(required, 'Closed recoverable case');
    for (const field of required) assert.ok(state[field], 'Required actual ' + field);
    if (['pipe-before-selection', 'abort-before-selection'].includes(state.name))
      assert.equal(state.allocation, false, 'No early allocation');
    if (state.name !== 'encoder-early-fault-gzip')
      assert.equal(ownerState.hasOperationFailure, state.name !== 'finish-cleanup-fault-gzip');
    if (state.sourceFault) assert.equal(ownerState.operationFailure, state.primary);
    if (state.endRejected) {
      assert.equal(ownerState.hasEndFailure, true);
      assert.equal(ownerState.endFailure, state.end);
    }
    if (state.cleanupFault) {
      assert.equal(ownerState.hasCleanupFailure, true);
      assert.equal(ownerState.cleanupFailure, state.cleanup);
    }
    if (state.name === 'encoder-early-fault-gzip') {
      assert.ok(state.encoderFault, 'Actual encoder error required');
      assert.equal(ownerState.hasCleanupFailure, true);
      assert.equal(ownerState.cleanupFailure, state.encoderFault);
    }
    assert.equal(ownerState.constructionUnconfirmed, false);
    assert.equal(joined, true, 'Recoverable case requires actual join');
    active = undefined;
    return Object.freeze({
      case: state.name,
      pipe: state.pipe,
      allocation: state.allocation,
      responseFinish: state.responseFinish,
      responseClose: state.responseClose,
      joined,
      constructionUnconfirmed: ownerState.constructionUnconfirmed,
    });
  }
  function unconfirmed(response, ownerState, joinPending) {
    const state = current(response);
    assert.equal(state.name, finalCase);
    assert.equal(state.pipe, true);
    assert.equal(state.allocation, false, 'No returned encoder');
    assert.equal(state.constructorFault, true);
    assert.equal(state.responseClose, true);
    assert.equal(state.handlerSettled, false, 'Actual handler remains pending on owner join');
    assert.equal(ownerState?.constructionUnconfirmed, true);
    assert.equal(ownerState.hasOperationFailure, true);
    assert.equal(ownerState.hasCleanupFailure, true);
    assert.equal(
      ownerState.operationFailure,
      state.constructorError,
      'Original constructor operation error identity',
    );
    assert.equal(
      ownerState.cleanupFailure,
      state.constructorError,
      'Original constructor cleanup error identity',
    );
    assert.equal(joinPending, true, 'No physical cleanup credit');
    return Object.freeze({
      case: finalCase,
      pipe: true,
      allocation: false,
      responseClose: true,
      handlerSettled: false,
      constructionUnconfirmed: true,
      joinPending: true,
    });
  }
  return Object.freeze({
    arm,
    route,
    routeAdapter,
    pipe,
    allocation,
    peerClose,
    sourceFault,
    bindEndWork,
    endRejected,
    responseFinish,
    responseClose,
    cleanupFault,
    encoderFault,
    constructorFault,
    handlerSettled,
    retire,
    unconfirmed,
    active: () => active !== undefined,
  });
}
