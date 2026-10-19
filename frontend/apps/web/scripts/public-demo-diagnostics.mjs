const stages = new Set(['proof', 'browser-spawn', 'startup-port', 'worker', 'result', 'cleanup']);
const reasons = new Set([
  'chromium-exited',
  'port-invalid',
  'deadline',
  'worker-failure',
  'unknown',
]);
const modes = new Set(['audit', 'browser', 'tls-refusal', 'timeout-control']);
const kinds = new Set(['catalog', 'course', 'lesson']);
const workerStages = new Set(['connect', 'preflight', 'audit', 'report']);
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

/** Explicit local outcomes, never classifications inferred from provider error text. */
export class PublicDemoFailure extends Error {
  constructor(reason) {
    super('Public demo proof failed');
    this.reason = reasons.has(reason) ? reason : 'unknown';
  }
}

export const publicDemoWorkerStage = (stage) => (workerStages.has(stage) ? stage : null);

/** Only closed phase/context and native process status may enter public artifacts. */
export function publicDemoFailure(stage, error, input = {}, child) {
  const phase = stages.has(stage) ? stage : 'proof';
  return {
    stage: phase,
    reason:
      error instanceof PublicDemoFailure && reasons.has(error.reason)
        ? error.reason
        : phase === 'worker'
          ? 'worker-failure'
          : 'unknown',
    mode: modes.has(input.mode) ? input.mode : null,
    lane:
      Number.isInteger(input.laneIndex) && input.laneIndex >= 0 && input.laneIndex < 3
        ? input.laneIndex + 1
        : null,
    kind: kinds.has(input.kind) ? input.kind : null,
    run: Number.isInteger(input.run) && input.run >= 1 && input.run <= 3 ? input.run : null,
    workerStage: phase === 'worker' ? publicDemoWorkerStage(input.workerStage) : null,
    exitCode:
      Number.isInteger(child?.exitCode) && child.exitCode >= 0 && child.exitCode <= 255
        ? child.exitCode
        : null,
    signal: signals.has(child?.signalCode) ? child.signalCode : null,
  };
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
