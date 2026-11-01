/**
 * Framework-owned response compression cleanup; inlined by the qualified generator.
 * No imports or application authority.
 * The factory contract is exactly one synchronous zlib constructor result.
 * Hidden resources in an arbitrary throwing factory are outside that contract.
 */
export function createResponseCompressionOwner(response) {
  const key = Symbol.for('learnstack.next.responseCompressionOwner');
  if (key in response) throw new Error('Duplicate response compression owner');

  let selection = 'unselected';
  let allocating = false;
  let constructionUnconfirmed = false;
  let sealed = false;
  let stream;
  let streamClosed = false;
  let responseTerminal = false;
  let settled = false;
  let drainListener;
  let drainRegistered = false;
  let hasOperationFailure = false;
  let operationFailure;
  let hasCleanupFailure = false;
  let cleanupFailure;
  let hasEndFailure = false;
  let endFailure;
  let resolveCleanup;
  let rejectCleanup;
  const cleanup = new Promise((resolve, reject) => {
    resolveCleanup = resolve;
    rejectCleanup = reject;
  });
  // Observe a failure immediately; joins still receive the original promise.
  cleanup.catch(() => {});

  function recordCleanupFailure(error) {
    if (!hasCleanupFailure) {
      hasCleanupFailure = true;
      cleanupFailure = error;
    }
  }

  function recordEndFailure(error) {
    if (!hasEndFailure) {
      hasEndFailure = true;
      endFailure = error;
    }
  }

  function removeOwnedListeners() {
    try {
      response.removeListener('finish', onResponseTerminal);
      response.removeListener('close', onResponseTerminal);
      if (drainRegistered) response.removeListener('drain', drainListener);
      if (stream) stream.removeListener('error', onStreamError);
    } catch (error) {
      recordCleanupFailure(error);
    }
  }

  function maybeSettle() {
    if (settled || allocating || constructionUnconfirmed) return;
    if (stream ? !streamClosed : selection === 'unselected' && !sealed) return;
    settled = true;
    removeOwnedListeners();
    if (hasCleanupFailure) rejectCleanup(cleanupFailure);
    else resolveCleanup();
  }

  function cancelStream() {
    if (!stream || streamClosed || stream.destroyed) return;
    try {
      stream.destroy();
    } catch (error) {
      // Failure to request destruction is not physical-close evidence.
      recordCleanupFailure(error);
    }
  }

  function cancelResponse() {
    if (response.destroyed) return;
    try {
      // The original fault is retained privately; no second error is emitted.
      response.destroy();
    } catch (error) {
      recordCleanupFailure(error);
    }
  }

  function onResponseTerminal() {
    responseTerminal = true;
    sealed = true;
    // A native refusal can finish before the allocated encoder has ever ended.
    cancelStream();
    maybeSettle();
  }

  function onStreamError(error) {
    recordCleanupFailure(error);
    sealed = true;
    cancelResponse();
    cancelStream();
    maybeSettle();
  }

  function onStreamClose() {
    streamClosed = true;
    maybeSettle();
  }

  function transitionFailure(error, abortResponse) {
    if (!hasOperationFailure) {
      hasOperationFailure = true;
      operationFailure = error;
    }
    sealed = true;
    if (stream || abortResponse) cancelResponse();
    cancelStream();
    maybeSettle();
  }

  function fail(error) {
    transitionFailure(error, false);
  }

  function abort(error) {
    // The boundary supplies its actual abort classification; no name heuristic.
    // Empty compression cleanup can retire while the native response-close
    // obligation remains independently owned by its original frame.
    transitionFailure(error, true);
  }

  function markNoCompression() {
    if (sealed && !stream && !allocating && !constructionUnconfirmed) {
      // A real no-resource failure can use the still-fenced uncompressed
      // fallback without reopening allocation or waiting for its own response.
      selection = 'none';
      maybeSettle();
      return;
    }
    if (selection !== 'unselected' || sealed || allocating) {
      throw new Error('Late or duplicate no-compression disposition');
    }
    selection = 'none';
    sealed = true;
    maybeSettle();
  }

  function createCompressor(factory) {
    if (selection !== 'unselected' || sealed || allocating) {
      throw new Error('Late or duplicate compression allocation');
    }
    if (typeof factory !== 'function') throw new TypeError('Compression constructor required');
    // Terminal/error reentrancy cannot acknowledge an empty allocation here.
    allocating = true;
    selection = 'allocating';
    let returned = false;
    try {
      stream = factory();
      returned = true;
      // Bound to the pinned actual zlib factory by the package transformation.
      if (!stream || typeof stream.once !== 'function' || typeof stream.destroy !== 'function') {
        throw new TypeError('Compression constructor did not return a stream');
      }
      stream.on('error', onStreamError);
      stream.once('close', onStreamClose);
      selection = 'allocated';
      if (responseTerminal || sealed || hasOperationFailure || hasCleanupFailure) {
        if (hasOperationFailure || hasCleanupFailure) cancelResponse();
        cancelStream();
      }
      return stream;
    } catch (error) {
      // An unreturned constructor can already own an opaque native handle.
      // There is no authority here to acknowledge its cleanup, even when one
      // experimental input is known to validate before allocation in Node.
      if (!returned || !stream) {
        constructionUnconfirmed = true;
        selection = 'construction-unconfirmed';
        recordCleanupFailure(error);
        cancelResponse();
      }
      fail(error);
      throw error;
    } finally {
      allocating = false;
      maybeSettle();
    }
  }

  function setResponseDrainListener(listener) {
    if (typeof listener !== 'function' || drainRegistered || !stream || streamClosed || settled) {
      throw new Error('Invalid response compression drain registration');
    }
    // The caller registers this exact listener immediately before native on().
    // Never register a user or foreign drain listener through this capability.
    drainRegistered = true;
    drainListener = listener;
    return listener;
  }

  const owner = Object.freeze({
    markNoCompression,
    allocationClosed: () => sealed,
    createCompressor,
    setResponseDrainListener,
    fail,
    abort,
    join: () => cleanup,
    recordCleanupFailure,
    recordEndFailure,
    failureState: () =>
      Object.freeze({
        hasOperationFailure,
        operationFailure,
        hasCleanupFailure,
        cleanupFailure,
        hasEndFailure,
        endFailure,
        constructionUnconfirmed,
      }),
  });
  Object.defineProperty(response, key, { value: owner });
  response.once('finish', onResponseTerminal);
  response.once('close', onResponseTerminal);
  // Already-destroyed response attachment closes allocation without pretending
  // to observe a finish event. No encoder can have been made through this slot.
  if (response.destroyed) {
    sealed = true;
    maybeSettle();
  }
  return owner;
}

/** Join actual operation/end work separately from physical encoder cleanup. */
export async function joinResponseCompression(response, actualOperation, explicitEndWork) {
  const key = Symbol.for('learnstack.next.responseCompressionOwner');
  let hasEndFailure = false;
  let endFailure;
  // Observe the explicitly passed task from entry, including a rejection before
  // writer.close is reached. This does not register arbitrary detached work.
  const endOutcome =
    explicitEndWork === undefined
      ? undefined
      : Promise.resolve(explicitEndWork).then(undefined, (error) => {
          hasEndFailure = true;
          endFailure = error;
          response[key]?.recordEndFailure(error);
        });
  let result;
  let hasPrimary = false;
  let primary;
  try {
    result = await (typeof actualOperation === 'function' ? actualOperation() : actualOperation);
  } catch (error) {
    hasPrimary = true;
    primary = error;
    // The boundary reports its failure before its own cleanup await.
    response[key]?.fail(error);
  }
  // Source/abort transitions above have already requested physical cancellation.
  // Transport or encoder closure cannot settle this independent actual task.
  if (endOutcome) await endOutcome;
  const owner = response[key];
  if (hasEndFailure) {
    // An operation may have installed the owner after the early observation.
    owner?.recordEndFailure(endFailure);
    if (!hasPrimary) owner?.fail(endFailure);
  }
  let cleanupFailed = false;
  let cleanupError;
  if (owner) {
    try {
      await owner.join();
    } catch (error) {
      cleanupFailed = true;
      cleanupError = error;
      owner.recordCleanupFailure(error);
    }
  }
  if (hasPrimary) throw primary;
  // Preserve existing upstream wrapping when the operation itself failed. A
  // previously unobserved end failure on a return path retains its exact value.
  if (hasEndFailure) throw endFailure;
  if (cleanupFailed) throw cleanupError;
  return result;
}
