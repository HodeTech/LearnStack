// Builtins-only runtime family: control the child, never signal its process group.
import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import {
  assertFrameworkProcessFamilySupport,
  FrameworkProcessFamilyError,
} from './framework-process-family.mjs';

const TAG = 'learnstack-runtime-v1';
const PROOF_TAG = 'learnstack-proof-v1';
const HEX = /^[0-9a-f]{32}$/;
const FAULTS = new Set(['startup-fault', 'listener-fault', 'protocol-fault', 'shutdown-fault']);
function exact(value, keys) {
  return (
    value !== null &&
    typeof value === 'object' &&
    !Array.isArray(value) &&
    Object.keys(value).sort().join(',') === [...keys].sort().join(',')
  );
}
function absent(group) {
  try {
    process.kill(-group, 0);
    return false;
  } catch (error) {
    if (error.code === 'ESRCH') return true;
    throw error;
  }
}
/** Resolve only after the direct child closes and its actual group disappears. */
export async function runFrameworkRuntimeFamily({
  entry,
  cwd,
  environment,
  mode,
  leaseRecord,
  signal: abortSignal,
  onFamily,
  proof,
} = {}) {
  assertFrameworkProcessFamilySupport();
  if (
    typeof entry !== 'string' ||
    typeof cwd !== 'string' ||
    !environment ||
    environment.NODE_OPTIONS ||
    !['production', 'development'].includes(mode) ||
    leaseRecord?.pid !== process.pid ||
    leaseRecord?.format !== 1 ||
    leaseRecord.kind !== mode ||
    (abortSignal !== undefined && !(abortSignal instanceof AbortSignal)) ||
    !HEX.test(leaseRecord?.nonce ?? '') ||
    (onFamily !== undefined && typeof onFamily !== 'function') ||
    (proof !== undefined && typeof proof?.attach !== 'function')
  )
    throw new FrameworkProcessFamilyError('runtime_arguments');
  if (abortSignal?.aborted) throw new FrameworkProcessFamilyError('failed');
  const challenge = randomBytes(16).toString('hex');
  const incarnation = randomBytes(16).toString('hex');
  const child = spawn(process.execPath, [entry], {
    cwd,
    env: {
      ...environment,
      LEARNSTACK_RUNTIME_CHALLENGE: challenge,
      LEARNSTACK_RUNTIME_PARENT: String(process.pid),
      LEARNSTACK_RUNTIME_MODE: mode,
      LEARNSTACK_RUNTIME_LEASE_NONCE: leaseRecord.nonce,
      LEARNSTACK_RUNTIME_INCARNATION: incarnation,
    },
    detached: true,
    stdio: ['inherit', 'inherit', 'inherit', 'ipc'],
  });
  return new Promise((resolve, reject) => {
    let handshakeValidated = false;
    let listenerReady = false;
    let requestedStop = false;
    let startupCancelled = false;
    let fault = null;
    let terminal;
    let code = null;
    let signal = null;
    let closed = false;
    let exited = false;
    let delay = 10;
    let observedError = false;
    let proofReceiver;
    let preHandshakeFault = false;
    const returnedWork = [];
    function trackUnexpected(result) {
      if (result === undefined) return;
      returnedWork.push(
        Promise.resolve(result).then(
          () => {},
          () => {},
        ),
      );
      throw new Error('Asynchronous runtime operation refused');
    }
    function stop() {
      if (!requestedStop && !listenerReady) startupCancelled = true;
      requestedStop = true;
      // The private challenge already binds this exact spawned channel. A
      // startup stop must reach the child latch before asynchronous validation
      // or the eventual hello can hold up its first-stop watchdog.
      if (!child.connected || exited || terminal) return;
      try {
        child.send({ tag: TAG, kind: 'stop', challenge, reason: 'abort' }, (error) => {
          if (error) fault ??= 'channel-fault';
        });
      } catch {
        fault ??= 'channel-fault';
      }
    }
    function protocolFault() {
      fault ??= 'protocol-fault';
      if (handshakeValidated) return stop();
      if (preHandshakeFault) return;
      preHandshakeFault = true;
      stop();
    }
    const abort = () => stop();
    abortSignal?.addEventListener('abort', abort, { once: true });
    if (abortSignal?.aborted) stop();
    function inspect() {
      if (!closed) return;
      if (child.pid === undefined) {
        abortSignal?.removeEventListener('abort', abort);
        reject(new FrameworkProcessFamilyError('spawn'));
        return;
      }
      let gone = false;
      try {
        gone = absent(child.pid);
      } catch {
        if (!observedError) {
          observedError = true;
          fault ??= 'observation-fault';
          process.stderr.write('Framework runtime family observation failed; ownership retained\n');
        }
      }
      if (!gone) {
        setTimeout(inspect, delay);
        delay = Math.min(delay * 2, 250);
        return;
      }
      void Promise.all(returnedWork).then(() => {
        abortSignal?.removeEventListener('abort', abort);
        if (!terminal) fault ??= 'channel-fault';
        resolve(
          Object.freeze({
            code,
            signal,
            handshakeValidated,
            listenerReady,
            requestedStop: requestedStop || Boolean(terminal?.requestedStop),
            startupCancelled:
              startupCancelled ||
              ((requestedStop || Boolean(terminal?.requestedStop)) && !terminal?.ready),
            parentLost: Boolean(terminal?.parentLost),
            fault: fault ?? terminal?.fault ?? null,
            directChildClosed: true,
            groupAbsent: true,
          }),
        );
      });
    }
    child.on('error', () => {
      fault ??= 'spawn-fault';
    });
    child.on('message', (message) => {
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
            protocolFault();
          }
        } else {
          protocolFault();
        }
        return;
      }
      if (message.challenge !== challenge || terminal) {
        protocolFault();
        return;
      }
      if (
        message.kind === 'hello' &&
        !handshakeValidated &&
        !preHandshakeFault &&
        child.connected &&
        exact(message, ['tag', 'kind', 'challenge', 'pid', 'parentPid', 'mode', 'incarnation']) &&
        message.pid === child.pid &&
        message.parentPid === process.pid &&
        message.mode === mode &&
        message.incarnation === incarnation
      ) {
        handshakeValidated = true;
        if (proof) {
          try {
            const proofFault = () => {
              fault ??= 'protocol-fault';
              stop();
            };
            const send = (frame) => {
              if (
                !exact(frame, ['tag', 'incarnation', 'payload']) ||
                frame.tag !== PROOF_TAG ||
                frame.incarnation !== incarnation ||
                !child.connected
              )
                throw new Error('Runtime proof envelope refused');
              child.send(frame, (error) => {
                if (error) proofFault();
              });
            };
            proofReceiver = proof.attach(
              Object.freeze({ send, childPid: child.pid, incarnation, onFault: proofFault }),
            );
            if (
              !proofReceiver ||
              typeof proofReceiver.receive !== 'function' ||
              typeof proofReceiver.then === 'function'
            ) {
              if (proofReceiver?.then)
                returnedWork.push(
                  Promise.resolve(proofReceiver).then(
                    () => {},
                    () => {},
                  ),
                );
              throw new Error('Runtime proof adapter refused');
            }
          } catch {
            fault ??= 'protocol-fault';
            stop();
          }
        }
        if (onFamily) {
          try {
            const result = onFamily(
              Object.freeze({
                pid: child.pid,
                group: child.pid,
                parentPid: process.pid,
                leaseNonce: leaseRecord.nonce,
                incarnation,
              }),
            );
            if (result !== undefined) {
              returnedWork.push(
                Promise.resolve(result).then(
                  () => {},
                  () => {},
                ),
              );
              throw new Error('Unexpected asynchronous runtime family observer');
            }
          } catch {
            fault ??= 'observer-fault';
            stop();
          }
        }
        if (requestedStop) stop();
        else {
          try {
            child.send({ tag: TAG, kind: 'start', challenge }, (error) => {
              if (error) {
                fault ??= 'channel-fault';
                stop();
              }
            });
          } catch {
            fault ??= 'channel-fault';
            stop();
          }
        }
        return;
      }
      if (
        message.kind === 'ready' &&
        handshakeValidated &&
        !listenerReady &&
        exact(message, ['tag', 'kind', 'challenge'])
      ) {
        listenerReady = true;
        return;
      }
      if (
        message.kind === 'terminal' &&
        (handshakeValidated ||
          (!preHandshakeFault && requestedStop && startupCancelled && message.ready === false)) &&
        exact(message, [
          'tag',
          'kind',
          'challenge',
          'ready',
          'requestedStop',
          'parentLost',
          'fault',
        ]) &&
        typeof message.ready === 'boolean' &&
        typeof message.requestedStop === 'boolean' &&
        typeof message.parentLost === 'boolean' &&
        (message.fault === null || FAULTS.has(message.fault)) &&
        message.ready === listenerReady &&
        message.requestedStop
      ) {
        terminal = message;
        return;
      }
      protocolFault();
    });
    child.once('disconnect', () => {
      if (!terminal && !exited) fault ??= 'channel-fault';
    });
    child.once('exit', () => {
      exited = true;
    });
    child.once('close', (actualCode, actualSignal) => {
      closed = true;
      code = actualCode;
      signal = actualSignal;
      inspect();
    });
  });
}
