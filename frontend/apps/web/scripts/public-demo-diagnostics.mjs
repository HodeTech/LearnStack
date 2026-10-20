import { DemoCommandError } from '../../../../scripts/demo-process.mjs';

const stages = new Set([
  'setup',
  'proof',
  'browser-spawn',
  'startup-port',
  'worker',
  'result',
  'cleanup',
]);
const reasons = new Set([
  'chromium-exited',
  'port-invalid',
  'deadline',
  'cancelled',
  'spawn',
  'output',
  'daemon',
  'worker-failure',
  'process-cleanup',
  'profile-cleanup',
  'cleanup-failure',
  'unknown',
]);
const modes = new Set(['audit', 'browser', 'tls-refusal', 'timeout-control']);
const kinds = new Set(['catalog', 'course', 'lesson']);
const workerStages = new Set(['input', 'connect', 'preflight', 'audit', 'report']);
const signals = new Set([
  'SIGABRT',
  'SIGBUS',
  'SIGFPE',
  'SIGHUP',
  'SIGILL',
  'SIGINT',
  'SIGKILL',
  'SIGPIPE',
  'SIGQUIT',
  'SIGSEGV',
  'SIGSYS',
  'SIGTERM',
  'SIGTRAP',
  'SIGXCPU',
  'SIGXFSZ',
]);
// Only this fixed portable diagnostic vocabulary reaches artifacts. Other native
// signals remain null; provider error text never supplies a signal classification.

/** Explicit local outcomes, never classifications inferred from provider error text. */
export class PublicDemoFailure extends Error {
  constructor(reason, cause) {
    super('Public demo proof failed', { cause });
    this.reason = reasons.has(reason) ? reason : 'unknown';
  }
}

export const publicDemoWorkerStage = (stage) => (workerStages.has(stage) ? stage : null);

/** Node records signal termination separately from a numeric exit code. */
export const publicDemoChildExited = (child) =>
  child.exitCode !== null || child.signalCode !== null;

/** Only closed phase/context and native process status may enter public artifacts. */
export function publicDemoFailure(stage, error, input = {}, child) {
  const phase = stages.has(stage) ? stage : 'proof';
  let reason =
    phase === 'cleanup' ? 'cleanup-failure' : phase === 'worker' ? 'worker-failure' : 'unknown';
  let process = null;
  let status;
  if (error instanceof PublicDemoFailure && reasons.has(error.reason)) reason = error.reason;
  if (error instanceof DemoCommandError) {
    if (error.origin === 'daemon') {
      reason = 'daemon';
      process = 'daemon';
      if (error.reason === 'exit') status = error;
    } else if (error.origin === 'command') {
      if (['deadline', 'cancelled', 'spawn', 'output'].includes(error.reason))
        reason = error.reason;
      if (error.reason === 'cleanup') reason = 'process-cleanup';
      if (phase === 'worker') {
        process = 'worker';
        if (error.reason === 'exit') status = error;
      } else if (['browser-spawn', 'startup-port'].includes(phase)) process = 'browser';
    }
  } else if (phase === 'startup-port' && reason === 'chromium-exited') {
    process = 'browser';
    status = child;
  }
  // Teardown's intentional SIGTERM never describes a browser/worker failure.
  if (phase === 'cleanup') {
    process = null;
    status = undefined;
  }
  return {
    stage: phase,
    reason,
    process,
    mode: modes.has(input.mode) ? input.mode : null,
    lane:
      Number.isInteger(input.laneIndex) && input.laneIndex >= 0 && input.laneIndex < 3
        ? input.laneIndex + 1
        : null,
    kind: kinds.has(input.kind) ? input.kind : null,
    run: Number.isInteger(input.run) && input.run >= 1 && input.run <= 3 ? input.run : null,
    workerStage: phase === 'worker' ? publicDemoWorkerStage(input.workerStage) : null,
    exitCode:
      Number.isInteger(status?.exitCode) && status.exitCode >= 0 && status.exitCode <= 255
        ? status.exitCode
        : null,
    signal: signals.has(status?.signalCode) ? status.signalCode : null,
  };
}

/** Emit only a complete validated worker line, even when dependency text shares a chunk. */
export function publicDemoWorkerOutput(chunk) {
  const lines = chunk.toString().split(/\r?\n/);
  return lines.filter((line) =>
    /^Public demo worker refused: (input|connect|preflight|audit|report|unknown)$/.test(line),
  );
}

/** Preserve the original failure when owned operation cleanup also refuses. */
export async function publicDemoOperation({ input, run, cleanup, child, record }) {
  let stage = 'browser-spawn';
  let failed = false;
  let failure;
  let result;
  try {
    result = await run((next) => {
      stage = stages.has(next) ? next : 'proof';
    });
  } catch (error) {
    failed = true;
    failure = error;
    record(publicDemoFailure(stage, error, input, child()));
  } finally {
    try {
      await cleanup();
    } catch (error) {
      record(publicDemoFailure('cleanup', error, input, child()));
      if (!failed) {
        failed = true;
        failure = error;
      }
    }
  }
  if (failed) throw failure;
  return result;
}
