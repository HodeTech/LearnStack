// Builtins-only private runtime controller. No generic compiler preload is used.
const TAG = 'learnstack-runtime-v1';
const PROOF_TAG = 'learnstack-proof-v1';
const HEX = /^[0-9a-f]{32}$/;
const ENV = [
  'LEARNSTACK_RUNTIME_CHALLENGE',
  'LEARNSTACK_RUNTIME_PARENT',
  'LEARNSTACK_RUNTIME_MODE',
  'LEARNSTACK_RUNTIME_LEASE_NONCE',
  'LEARNSTACK_RUNTIME_INCARNATION',
];

function exact(value, keys) {
  return (
    value !== null &&
    typeof value === 'object' &&
    !Array.isArray(value) &&
    Object.keys(value).sort().join(',') === [...keys].sort().join(',')
  );
}
/** Install the irreversible latch before any asynchronous private validation. */
export function createFrameworkRuntimeChildControl() {
  const [challenge, rawParent, mode, leaseNonce, incarnation] = ENV.map((key) => process.env[key]);
  for (const key of ENV) delete process.env[key];
  const parentPid = Number(rawParent);
  if (
    !HEX.test(challenge ?? '') ||
    !HEX.test(leaseNonce ?? '') ||
    !HEX.test(incarnation ?? '') ||
    !/^[1-9][0-9]*$/.test(rawParent ?? '') ||
    !Number.isSafeInteger(parentPid) ||
    parentPid !== process.ppid ||
    !['production', 'development'].includes(mode) ||
    typeof process.send !== 'function' ||
    !process.connected
  )
    throw new Error('Framework private runtime control refused');
  const binding = Object.freeze({ parentPid, mode, leaseNonce, incarnation });
  let stopped = false;
  let parentLost = false;
  let fault = null;
  let ready = false;
  let readyBeforeStop = false;
  let registered;
  let shutdownStarted = false;
  let watchdog;
  let admitted = false;
  let starting;
  let resolveStart;
  let terminal = false;
  let proofReceiver;
  let proofDrainStarted = false;
  let returnedWork = 0;
  let deferredExit;

  function trackUnexpected(result) {
    if (result === undefined) return;
    returnedWork++;
    Promise.resolve(result).then(settled, settled);
    throw new Error('Asynchronous runtime proof operation refused');
  }
  function settled() {
    returnedWork--;
    if (returnedWork === 0 && deferredExit !== undefined) finish(deferredExit);
  }

  function send(message, callback) {
    if (!process.connected) return callback?.(new Error('channel'));
    try {
      process.send({ tag: TAG, ...message, challenge }, callback);
    } catch (error) {
      callback?.(error);
    }
  }
  function finish(code) {
    if (terminal) return;
    if (returnedWork) {
      deferredExit = code;
      return;
    }
    // A copied profile may run a real asynchronous command (for example a
    // framework trace flush or the native completion barrier). Its private
    // drain joins that issued work; it is never exposed to native application
    // code, and the original first-stop watchdog continues to bound the child.
    if (!proofDrainStarted && typeof proofReceiver?.drain === 'function') {
      proofDrainStarted = true;
      returnedWork++;
      Promise.resolve()
        .then(() => proofReceiver.drain())
        .then(
          () => {
            returnedWork--;
            finish(code);
          },
          () => {
            fault ??= 'protocol-fault';
            returnedWork--;
            finish(1);
          },
        );
      return;
    }
    terminal = true;
    const frame = {
      kind: 'terminal',
      ready: readyBeforeStop,
      requestedStop: stopped,
      parentLost,
      fault,
    };
    // Flush the terminal observation on the real channel before expected exit.
    if (!process.connected) return process.exit(code || 1);
    send(frame, (error) => {
      if (watchdog) clearTimeout(watchdog);
      process.exit(error ? 1 : code);
    });
  }
  function invokeShutdown() {
    if (!stopped || !registered || shutdownStarted) return;
    shutdownStarted = true;
    Promise.resolve()
      .then(registered)
      .then(
        () => finish(fault || parentLost || !readyBeforeStop ? 1 : 0),
        () => {
          fault ??= 'shutdown-fault';
          finish(1);
        },
      );
  }
  function requestStop(reason) {
    if (
      ![
        'signal',
        'abort',
        'parent-lost',
        'startup-fault',
        'listener-fault',
        'protocol-fault',
      ].includes(reason)
    )
      reason = 'protocol-fault';
    if (reason === 'parent-lost') parentLost = true;
    if (['startup-fault', 'listener-fault', 'protocol-fault'].includes(reason)) fault ??= reason;
    if (!stopped) {
      stopped = true;
      readyBeforeStop = ready;
      watchdog = setTimeout(() => {
        // A stalled IPC flush must not extend the original shutdown deadline.
        process.exit(1);
      }, 5000);
    }
    if (resolveStart) {
      resolveStart(false);
      resolveStart = undefined;
    }
    invokeShutdown();
  }
  function listenerReady(server) {
    if (stopped || ready || !admitted) {
      if (!stopped) requestStop('protocol-fault');
      return false;
    }
    const address = server?.address?.();
    if (
      !server?.listening ||
      !address ||
      typeof address !== 'object' ||
      address.address !== '127.0.0.1' ||
      !Number.isInteger(address.port) ||
      address.port < 1 ||
      address.port > 65535
    ) {
      requestStop('listener-fault');
      return false;
    }
    ready = true;
    send({ kind: 'ready' }, (error) => {
      if (error) requestStop('parent-lost');
    });
    return true;
  }
  const terminate = () => requestStop('signal');
  process.on('SIGTERM', terminate);
  process.on('SIGINT', terminate);
  process.once('disconnect', () => requestStop('parent-lost'));
  process.on('message', (message) => {
    if (message?.tag !== TAG) {
      if (
        proofReceiver &&
        exact(message, ['tag', 'incarnation', 'payload']) &&
        message.tag === PROOF_TAG &&
        message.incarnation === incarnation
      ) {
        try {
          trackUnexpected(proofReceiver.receive(message));
        } catch {
          requestStop('protocol-fault');
        }
      } else requestStop('protocol-fault');
      return;
    }
    if (message.challenge !== challenge) return requestStop('protocol-fault');
    if (
      message.kind === 'stop' &&
      exact(message, ['tag', 'kind', 'challenge', 'reason']) &&
      ['signal', 'abort'].includes(message.reason)
    )
      return requestStop(message.reason);
    if (
      message.kind === 'start' &&
      exact(message, ['tag', 'kind', 'challenge']) &&
      starting &&
      !admitted &&
      resolveStart
    ) {
      const resolve = resolveStart;
      resolveStart = undefined;
      if (!stopped) {
        admitted = true;
        resolve(true);
      } else resolve(false);
      return;
    }
    requestStop('protocol-fault');
  });
  async function admit(framework, { proof } = {}) {
    if (starting) throw new Error('Framework private runtime control already admitted');
    if (!framework || framework.mode !== mode || !framework.identity)
      throw new Error('Framework private runtime identity refused');
    const value = Object.freeze({ identity: framework.identity, mode });
    if (stopped) {
      finish(1);
      return null;
    }
    starting = new Promise((resolve) => {
      resolveStart = resolve;
    });
    send({ kind: 'hello', pid: process.pid, parentPid, mode, incarnation }, (error) => {
      if (error) requestStop('parent-lost');
    });
    if (!(await starting)) {
      finish(1);
      return null;
    }
    if (proof !== undefined) {
      try {
        if (typeof proof?.attach !== 'function') throw new Error('Runtime proof adapter refused');
        const sendProof = (frame) => {
          if (
            !exact(frame, ['tag', 'incarnation', 'payload']) ||
            frame.tag !== PROOF_TAG ||
            frame.incarnation !== incarnation ||
            !process.connected
          )
            throw new Error('Runtime proof envelope refused');
          process.send(frame, (error) => {
            if (error) requestStop('parent-lost');
          });
        };
        proofReceiver = proof.attach(Object.freeze({ send: sendProof, incarnation, requestStop }));
        if (
          !proofReceiver ||
          typeof proofReceiver.receive !== 'function' ||
          typeof proofReceiver.emit !== 'function' ||
          typeof proofReceiver.onCommand !== 'function' ||
          typeof proofReceiver.then === 'function'
        ) {
          if (proofReceiver?.then) trackUnexpected(proofReceiver);
          throw new Error('Runtime proof adapter refused');
        }
      } catch {
        requestStop('protocol-fault');
        finish(1);
        return null;
      }
    }
    const nativeControl = {
      framework: value,
      registerShutdown(fn) {
        if (typeof fn !== 'function' || registered)
          throw new Error('Runtime shutdown registration refused');
        registered = fn;
        invokeShutdown();
      },
      requestStop,
      stopRequested: () => stopped,
      listenerReady,
    };
    if (proofReceiver)
      nativeControl.proof = Object.freeze({
        emit: (payload) => proofReceiver.emit(payload),
        onCommand: (fn) => proofReceiver.onCommand(fn),
      });
    return Object.freeze(nativeControl);
  }
  function fail(reason = 'startup-fault') {
    requestStop(reason);
    if (!registered) finish(1);
  }
  return Object.freeze({ binding, admit, fail });
}
