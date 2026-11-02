// Test-only finite failure observations. They never establish cleanup authority.
import { writeSync } from 'node:fs';

const cases = [
  'ready-term',
  'ready-int',
  'prepare-term',
  'prepare-int',
  'prepare-watchdog',
  'registration-release',
  'registration-watchdog',
  'prehello-term',
  'prehello-int',
  'prehello-child-term',
  'prehello-child-int',
  'prehello-owner-death',
  'prepare-owner-death',
  'prepare-disconnect',
  'worker-positive',
  'worker-exit-zero',
  'worker-exit-one',
  'worker-self-kill',
  'worker-ordered-stop',
  'worker-owner-death',
] as const;
const reasons = [
  'unobserved',
  'owner_busy',
  'graph_identity',
  'development_identity',
  'development_compiler',
  'producer_failed',
  'framework-other',
  'runtime-other',
] as const;
const scripts = [
  'framework-router-fault.test.mjs',
  'framework-router-fault-gate.test.mjs',
  'framework-router-registration.test.mjs',
  'framework-router-next-chain.test.mjs',
  'framework-router-fault-prefix.test.mjs',
  'framework-router-registration-capture.test.mjs',
] as const;
const signals = ['SIGTERM', 'SIGINT', 'SIGKILL'] as const;
const spawnCodes = ['ENOENT', 'EACCES', 'EAGAIN', 'EMFILE', 'ENFILE', 'ENOMEM', 'ENOBUFS'] as const;
const stages: readonly string[] = [
  'build',
  'cleanup',
  'restart:build',
  'worker-ordered-stop:hold-real-render',
  ...cases.flatMap((name) =>
    ['start', 'stop', 'direct-exit', 'worker-release'].map((phase) => `${name}:${phase}`),
  ),
];
const has = (values: readonly string[], value: unknown): value is string =>
  typeof value === 'string' && values.includes(value);

/** Parse only the parent matrix's already-validated finite projection. */
export function ownershipDiagnostic(raw: unknown, group: string) {
  try {
    if (typeof raw !== 'string' || Buffer.byteLength(raw, 'utf8') > 2048) return null;
    const value: unknown = JSON.parse(raw);
    if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
    const row = value as Record<string, unknown>;
    if (
      Object.keys(row).sort().join(',') !==
      'admissionReason,cleanupConfirmed,currentCase,group,retained,stage'
    )
      return null;
    if (
      !has(['startup', 'workers', 'recovery'], group) ||
      row.group !== group ||
      !has(stages, row.stage) ||
      !(row.currentCase === null || has(cases, row.currentCase)) ||
      !(row.admissionReason === null || has(reasons, row.admissionReason)) ||
      typeof row.cleanupConfirmed !== 'boolean' ||
      typeof row.retained !== 'boolean'
    )
      return null;
    if (row.currentCase === null && row.admissionReason !== null) return null;
    if (
      row.stage === 'worker-ordered-stop:hold-real-render' &&
      (group !== 'workers' || row.currentCase !== 'worker-ordered-stop')
    )
      return null;
    return {
      schema: 1,
      kind: 'framework-ownership-failure',
      group,
      stage: row.stage,
      currentCase: row.currentCase,
      admissionReason: row.admissionReason,
      cleanupConfirmed: row.cleanupConfirmed,
      retained: row.retained,
    };
  } catch {
    return null;
  }
}

/** Only fixed script names and primitive finite process fields are projected. */
export function routerDiagnostic(script: unknown, error: unknown, elapsed: number) {
  try {
    if (!has(scripts, script) || !error || typeof error !== 'object') return null;
    const row = error as Record<string, unknown>;
    const rawStatus = row.status;
    const rawSignal = row.signal;
    const rawCode = row.code;
    const status =
      typeof rawStatus === 'number' &&
      Number.isSafeInteger(rawStatus) &&
      rawStatus >= 0 &&
      rawStatus <= 255
        ? rawStatus
        : null;
    const signal = has(signals, rawSignal) ? rawSignal : null;
    const kind =
      rawCode === 'ETIMEDOUT'
        ? 'timeout'
        : status !== null
          ? 'status'
          : signal !== null
            ? 'signal'
            : has(spawnCodes, rawCode)
              ? 'spawn'
              : 'unknown';
    const elapsedMs =
      Number.isFinite(elapsed) && elapsed >= 0 && elapsed <= 180_000 ? Math.round(elapsed) : null;
    return {
      schema: 1,
      kind: 'framework-router-control-failure',
      script,
      class: kind,
      status,
      signal,
      elapsedMs,
    };
  } catch {
    return null;
  }
}
/** Report only the fixed failing TAP counter; never inspect or publish stdout. */
export function routerCountDiagnostic(script: unknown, counter: unknown, elapsed: number) {
  if (
    !has(scripts, script) ||
    !has(['tests', 'pass', 'fail', 'cancelled', 'skipped', 'todo'], counter)
  )
    return null;
  const elapsedMs =
    Number.isFinite(elapsed) && elapsed >= 0 && elapsed <= 180_000 ? Math.round(elapsed) : null;
  return {
    schema: 1,
    kind: 'framework-router-control-failure',
    script,
    class: 'count-mismatch',
    counter,
    elapsedMs,
  };
}
function emit(value: object | null) {
  if (value === null) return;
  // A diagnostic failure never changes the failed proof or exports its cause.
  try {
    writeSync(2, `\nFrontend proof diagnostic: ${JSON.stringify(value)}\n`);
  } catch {
    /* closed */
  }
}
export const emitOwnershipDiagnostic = (raw: unknown, group: string) =>
  emit(ownershipDiagnostic(raw, group));
export const emitRouterDiagnostic = (script: unknown, error: unknown, elapsed: number) =>
  emit(routerDiagnostic(script, error, elapsed));
export const emitRouterCountDiagnostic = (script: unknown, counter: unknown, elapsed: number) =>
  emit(routerCountDiagnostic(script, counter, elapsed));
