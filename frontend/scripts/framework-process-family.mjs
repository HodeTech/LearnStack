// Builtins only. A direct compiler/manager exit does not retire its descendants.
import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';

const PRELOAD = new URL('./framework-process-family-preload.mjs', import.meta.url).href;

export class FrameworkProcessFamilyError extends Error {
  constructor(reason) {
    super(`Framework process family refused: ${reason}`);
    this.name = 'FrameworkProcessFamilyError';
    this.reason = reason;
  }
}

export function assertFrameworkProcessFamilySupport() {
  // These are the currently exercised workstation/hosted-CI process surfaces.
  // Do not silently substitute direct-child observation on another platform.
  if (!['darwin', 'linux'].includes(process.platform))
    throw new FrameworkProcessFamilyError('platform');
}

function groupPresent(group) {
  try {
    process.kill(-group, 0);
    return true;
  } catch (error) {
    if (error.code === 'ESRCH') return false;
    throw error;
  }
}

/**
 * The qualified Node/Next/pnpm children inherit this private POSIX group. No
 * supported compiler/lifecycle path detaches a mutable descendant from it.
 * Timers only re-observe actual group existence; they never acknowledge cleanup.
 */
export async function runFrameworkProcessFamily(
  command,
  args,
  { cwd, environment, stdio = 'inherit', signal: abortSignal, onFamily } = {},
) {
  assertFrameworkProcessFamilySupport();
  if (command !== process.execPath || !['inherit', 'ignore'].includes(stdio))
    throw new FrameworkProcessFamilyError('command');
  if (abortSignal !== undefined && !(abortSignal instanceof AbortSignal))
    throw new FrameworkProcessFamilyError('signal');
  if (abortSignal?.aborted) throw new FrameworkProcessFamilyError('failed');
  if (onFamily !== undefined && typeof onFamily !== 'function')
    throw new FrameworkProcessFamilyError('observer');
  return new Promise((resolve, reject) => {
    const nonce = randomBytes(16).toString('hex');
    const child = spawn(command, ['--import', PRELOAD, ...args], {
      cwd,
      env: {
        ...environment,
        LEARNSTACK_FRAMEWORK_FAMILY_NONCE: nonce,
        LEARNSTACK_FRAMEWORK_FAMILY_PARENT: String(process.pid),
      },
      detached: true,
      stdio: [stdio, stdio, stdio, 'ipc'],
    });
    let exited = false;
    let closed = false;
    let code;
    let signal;
    let fault;
    let cancelled = false;
    let ready = false;
    let requested;
    let observationFailed = false;
    let observerWork = Promise.resolve();
    const sendRequest = () => {
      if (!requested || !ready || exited || !child.connected) return;
      try {
        child.send(
          { kind: 'learnstack-framework-family-stop', nonce, signal: requested },
          (error) => {
            if (error) fault ??= error;
          },
        );
      } catch (error) {
        fault ??= error;
      }
    };
    const cancel = (signal) => {
      if (cancelled) return;
      cancelled = true;
      requested = signal;
      // Private IPC addresses the exact child channel. Only the live child
      // signals its own current group; the owner never signals a cached PGID.
      sendRequest();
    };
    const terminate = () => cancel('SIGTERM');
    const interrupt = () => cancel('SIGINT');
    process.on('SIGTERM', terminate);
    process.on('SIGINT', interrupt);
    abortSignal?.addEventListener('abort', terminate, { once: true });
    if (abortSignal?.aborted) terminate();
    const inspect = () => {
      if (!closed) return;
      let present;
      try {
        present = child.pid !== undefined && groupPresent(child.pid);
      } catch {
        // An unobservable family retains the surrounding durable lease. There
        // is deliberately no timeout that turns this uncertainty into release.
        present = true;
        if (!observationFailed) {
          observationFailed = true;
          process.stderr.write('Framework process family observation failed; ownership retained\n');
        }
      }
      if (present) {
        setTimeout(inspect, 10);
        return;
      }
      // The observer violated its synchronous contract, but its returned work
      // still belongs to this owner. Neither a rejection nor group absence
      // permits release while that work remains outstanding.
      void observerWork.then(() => {
        process.removeListener('SIGTERM', terminate);
        process.removeListener('SIGINT', interrupt);
        abortSignal?.removeEventListener('abort', terminate);
        if (fault || !ready || cancelled || code !== 0 || signal !== null)
          reject(new FrameworkProcessFamilyError('failed'));
        else resolve();
      });
    };
    child.on('error', (error) => {
      fault ??= error;
    });
    child.on('message', (message) => {
      if (message?.kind !== 'learnstack-framework-family-ready') return;
      if (
        ready ||
        message.nonce !== nonce ||
        message.pid !== child.pid ||
        message.parent !== process.pid ||
        Object.keys(message).length !== 4
      ) {
        fault ??= new FrameworkProcessFamilyError('handshake');
        return;
      }
      ready = true;
      if (onFamily) {
        try {
          const result = onFamily(
            Object.freeze({
              pid: child.pid,
              group: child.pid,
              parentPid: process.pid,
            }),
          );
          if (result !== undefined) {
            observerWork = Promise.resolve(result).then(
              () => {},
              () => {},
            );
            throw new FrameworkProcessFamilyError('observer');
          }
        } catch (error) {
          fault ??= error;
          cancel('SIGTERM');
        }
      }
      sendRequest();
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
