import assert from 'node:assert/strict';
import test from 'node:test';
import zlib from 'node:zlib';

import {
  createRouterFaultGate,
  faultCases,
  finalCase,
  recoverableCases,
} from '../apps/web/scripts/framework-router-fault-gate.mjs';
import { copiedRouterFaultRouteSource } from '../apps/web/scripts/framework-router-fault-route.mjs';

test('finite case set keeps the unconfirmed constructor final', () => {
  assert.equal(new Set(faultCases).size, faultCases.length);
  assert.equal(faultCases.at(-1), finalCase);
  assert.equal(recoverableCases.includes(finalCase), false);
  assert.equal(
    (copiedRouterFaultRouteSource.match(/pendingRevalidateWrites\.push\(pending\)/g) ?? []).length,
    1,
  );
  assert.equal((copiedRouterFaultRouteSource.match(/fault\.pipe/g) ?? []).length, 1);
  assert.equal((copiedRouterFaultRouteSource.match(/fault\.allocation/g) ?? []).length, 2);
});

test('preselection fault needs same response router and pipe, then real close and join', async () => {
  const gate = createRouterFaultGate();
  const response = {};
  const foreign = {};
  const armed = gate.arm('pipe-before-selection', response);
  assert.throws(() => gate.pipe(response), /router before pipe/);
  gate.route(response);
  assert.throws(() => gate.pipe(foreign), /Exact active native response/);
  gate.pipe(response);
  await armed.pipe;
  assert.throws(() => gate.allocation(response));
  assert.throws(() => gate.sourceFault(response, new Error('lookalike')), /identity/);
  gate.sourceFault(response, armed.errors.primary);
  gate.responseClose(response);
  gate.handlerSettled(response);
  assert.deepEqual(
    gate.retire(
      response,
      {
        hasOperationFailure: true,
        operationFailure: armed.errors.primary,
        hasCleanupFailure: false,
        hasEndFailure: false,
        constructionUnconfirmed: false,
      },
      true,
    ),
    {
      case: 'pipe-before-selection',
      pipe: true,
      allocation: false,
      responseFinish: false,
      responseClose: true,
      joined: true,
      constructionUnconfirmed: false,
    },
  );
  assert.equal(gate.active(), false);
});

test('source fault observation cannot be omitted from a generic owner failure', () => {
  const gate = createRouterFaultGate();
  const response = {};
  const armed = gate.arm('source-fault-gzip', response);
  gate.route(response);
  gate.pipe(response);
  gate.allocation(response);
  gate.responseClose(response);
  gate.handlerSettled(response);
  const state = {
    hasOperationFailure: true,
    operationFailure: armed.errors.primary,
    hasCleanupFailure: false,
    hasEndFailure: false,
    constructionUnconfirmed: false,
  };
  assert.throws(() => gate.retire(response, state, true), /Required actual sourceFault/);
  gate.sourceFault(response, armed.errors.primary);
  assert.equal(gate.retire(response, state, true).joined, true);
});

test('peer abort cannot borrow source fault or another response', async () => {
  const gate = createRouterFaultGate();
  const response = {};
  const armed = gate.arm('abort-before-selection', response);
  gate.route(response);
  gate.pipe(response);
  assert.throws(() => gate.sourceFault(response, armed.errors.primary));
  assert.throws(() => gate.peerClose({}), /Exact active native response/);
  gate.peerClose(response);
  await armed.peerClose;
  assert.throws(() => gate.peerClose(response), /One actual peerClose/);
});

test('abort requires its own peer-close witness before retirement', () => {
  const gate = createRouterFaultGate();
  const response = {};
  gate.arm('abort-before-selection', response);
  gate.route(response);
  gate.pipe(response);
  gate.responseClose(response);
  gate.handlerSettled(response);
  const state = {
    hasOperationFailure: true,
    hasCleanupFailure: false,
    hasEndFailure: false,
    constructionUnconfirmed: false,
  };
  assert.throws(() => gate.retire(response, state, true), /Required actual peerClose/);
  gate.peerClose(response);
  assert.equal(gate.retire(response, state, true).allocation, false);
});

test('route adapter is bound to the exact arm and refuses a second GET', () => {
  const gate = createRouterFaultGate();
  const response = {};
  const armed = gate.arm('source-fault-gzip', response);
  assert.throws(() => gate.routeAdapter({}), /Exact active native response/);
  const adapter = gate.routeAdapter(response);
  const route = adapter.begin();
  assert.equal(route.errors.primary, armed.errors.primary);
  assert.equal(route.pipe, armed.pipe);
  assert.throws(() => adapter.begin(), /One actual route/);
  assert.throws(() => gate.arm('source-fault-gzip', {}), /One active request/);
});

test('end failure must be the promise in the actual Next work store after allocation', () => {
  const gate = createRouterFaultGate();
  const response = {};
  const armed = gate.arm('end-work-reject-gzip', response);
  const pending = new Promise(() => {});
  const store = { pendingRevalidateWrites: [pending] };
  gate.route(response);
  gate.pipe(response);
  gate.bindEndWork(response, store, pending);
  assert.throws(() => gate.endRejected(response, armed.errors.end, store));
  gate.allocation(response);
  gate.responseClose(response);
  gate.handlerSettled(response);
  const ownerState = {
    hasOperationFailure: true,
    hasCleanupFailure: false,
    hasEndFailure: true,
    endFailure: armed.errors.end,
    constructionUnconfirmed: false,
  };
  assert.throws(() => gate.retire(response, ownerState, true), /Required actual endRejected/);
  assert.throws(() =>
    gate.endRejected(response, armed.errors.end, { pendingRevalidateWrites: [pending] }),
  );
  gate.endRejected(response, armed.errors.end, store);
  assert.throws(
    () => gate.endRejected(response, armed.errors.end, store),
    /One actual endRejected/,
  );
  assert.equal(gate.retire(response, ownerState, true).joined, true);
});

test('cleanup identity is separate and finish cleanup requires actual finish', () => {
  const gate = createRouterFaultGate();
  const response = {};
  const armed = gate.arm('finish-cleanup-fault-gzip', response);
  gate.route(response);
  gate.pipe(response);
  gate.allocation(response);
  assert.throws(() => gate.cleanupFault(response, armed.errors.cleanup));
  gate.responseFinish(response);
  assert.throws(() => gate.cleanupFault(response, armed.errors.primary), /identity/);
  gate.responseClose(response);
  gate.handlerSettled(response);
  assert.throws(
    () =>
      gate.retire(
        response,
        {
          hasOperationFailure: false,
          hasCleanupFailure: true,
          cleanupFailure: armed.errors.cleanup,
          hasEndFailure: false,
          constructionUnconfirmed: false,
        },
        true,
      ),
    /Required actual cleanupFault/,
  );
  gate.cleanupFault(response, armed.errors.cleanup);
  assert.throws(
    () =>
      gate.retire(
        response,
        {
          hasOperationFailure: false,
          hasCleanupFailure: true,
          cleanupFailure: armed.errors.primary,
          hasEndFailure: false,
          constructionUnconfirmed: false,
        },
        true,
      ),
    { code: 'ERR_ASSERTION' },
  );
});

test('early encoder fault is emitted by an actual zlib stream and closes it', async () => {
  const gate = createRouterFaultGate();
  const response = {};
  gate.arm('encoder-early-fault-gzip', response);
  gate.route(response);
  gate.pipe(response);
  gate.allocation(response);
  const stream = zlib.createGzip();
  const events = [];
  stream.once('error', (error) => {
    events.push('error');
    gate.encoderFault(response, error);
  });
  const closed = new Promise((resolve) => stream.once('close', resolve));
  stream.end();
  stream.write(Buffer.from('x'));
  await closed;
  events.push('close');
  assert.deepEqual(events, ['error', 'close']);
});

test('throwing constructor keeps handler and owner join pending without cleanup credit', () => {
  const gate = createRouterFaultGate();
  const response = {};
  gate.arm(finalCase, response);
  gate.route(response);
  gate.pipe(response);
  const native = new RangeError('Invalid compression level');
  native.code = 'ERR_OUT_OF_RANGE';
  gate.constructorFault(response, native);
  gate.responseClose(response);
  const state = {
    hasOperationFailure: true,
    operationFailure: native,
    hasCleanupFailure: true,
    cleanupFailure: native,
    hasEndFailure: false,
    constructionUnconfirmed: true,
  };
  assert.throws(() => gate.retire(response, state, true), /cannot retire cleanly/);
  assert.throws(() => gate.unconfirmed(response, state, false), /No physical cleanup credit/);
  assert.throws(
    () =>
      gate.unconfirmed(response, { ...state, operationFailure: new RangeError('lookalike') }, true),
    /identity/,
  );
  assert.throws(
    () =>
      gate.unconfirmed(response, { ...state, cleanupFailure: new RangeError('lookalike') }, true),
    /identity/,
  );
  assert.deepEqual(gate.unconfirmed(response, state, true), {
    case: finalCase,
    pipe: true,
    allocation: false,
    responseClose: true,
    handlerSettled: false,
    constructionUnconfirmed: true,
    joinPending: true,
  });
});
