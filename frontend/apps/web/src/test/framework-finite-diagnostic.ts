// Test-only finite failure observations. They never establish cleanup authority.
import { writeSync } from 'node:fs';
import { TextDecoder } from 'node:util';

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
const readinessReasons = ['deadline', 'closed', 'error', 'shape'] as const;
const ownerSignals = ['none', 'SIGINT', 'SIGTERM', 'SIGKILL', 'other'] as const;
const operations = [
  'build',
  'launch',
  'ready',
  'http-root',
  'signal',
  'retire',
  'row',
  'worker-request',
  'worker-entered',
  'worker-identity',
  'records',
  'worker-fault',
  'runtime-absent',
  'competitor',
  'worker-release',
  'worker-response',
  'worker-returned',
  'owner-completed',
  'restart-build',
  'cleanup',
] as const;
const failureCodes = [
  'ERR_ASSERTION',
  'ENOENT',
  'ESRCH',
  'EPERM',
  'EACCES',
  'ETIMEDOUT',
  'ECONNRESET',
  'ECONNREFUSED',
  'EPIPE',
] as const;
const completedCaseMaximum: Readonly<Record<string, number>> = {
  startup: 17,
  workers: 5,
  recovery: 7,
};
const scripts = [
  'framework-router-fault.test.mjs',
  'framework-router-fault-gate.test.mjs',
  'framework-router-registration.test.mjs',
  'framework-router-next-chain.test.mjs',
  'framework-router-fault-prefix.test.mjs',
  'framework-router-registration-capture.test.mjs',
] as const;
const scriptCaps: Readonly<Record<string, number>> = {
  'framework-router-fault.test.mjs': 31,
  'framework-router-fault-gate.test.mjs': 10,
  'framework-router-registration.test.mjs': 4,
  'framework-router-next-chain.test.mjs': 3,
  'framework-router-fault-prefix.test.mjs': 13,
  'framework-router-registration-capture.test.mjs': 9,
};
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

// Owner/proof-channel observations only; never actual runtime-child retirement.
function readinessDiagnostic(value: unknown) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
  const row = value as Record<string, unknown>;
  if (Object.keys(row).sort().join(',') !== 'elapsedMilliseconds,ownerExitCode,ownerSignal,reason')
    return null;
  const { reason, elapsedMilliseconds, ownerExitCode, ownerSignal } = row;
  if (
    !has(readinessReasons, reason) ||
    typeof elapsedMilliseconds !== 'number' ||
    !Number.isSafeInteger(elapsedMilliseconds) ||
    elapsedMilliseconds < 0 ||
    elapsedMilliseconds > 120000 ||
    !(
      ownerExitCode === null ||
      (typeof ownerExitCode === 'number' &&
        Number.isSafeInteger(ownerExitCode) &&
        ownerExitCode >= 0 &&
        ownerExitCode <= 255)
    ) ||
    !has(ownerSignals, ownerSignal)
  )
    return null;
  return { reason, elapsedMilliseconds, ownerExitCode, ownerSignal };
}

/** Parse only the parent matrix's already-validated finite projection. */
export function ownershipDiagnostic(raw: unknown, group: string) {
  try {
    if (typeof raw !== 'string' || Buffer.byteLength(raw, 'utf8') > 2048) return null;
    const value: unknown = JSON.parse(raw);
    if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
    const row = value as Record<string, unknown>;
    if (
      Object.keys(row).sort().join(',') !==
      'admissionReason,cleanupConfirmed,completedCaseCount,currentCase,currentMode,failureCode,group,operation,readiness,retained,rootHttpStatus,stage'
    )
      return null;
    if (
      !has(['startup', 'workers', 'recovery'], group) ||
      row.group !== group ||
      !has(stages, row.stage) ||
      !(row.currentCase === null || has(cases, row.currentCase)) ||
      !(row.currentMode === null || has(['production', 'development'], row.currentMode)) ||
      (row.currentCase === null) !== (row.currentMode === null) ||
      typeof row.completedCaseCount !== 'number' ||
      !Number.isSafeInteger(row.completedCaseCount) ||
      row.completedCaseCount < 0 ||
      row.completedCaseCount > (completedCaseMaximum[group] ?? -1) ||
      !has(operations, row.operation) ||
      !(
        row.rootHttpStatus === null ||
        (typeof row.rootHttpStatus === 'number' &&
          Number.isSafeInteger(row.rootHttpStatus) &&
          row.rootHttpStatus >= 100 &&
          row.rootHttpStatus <= 599)
      ) ||
      (row.currentCase === null && row.rootHttpStatus !== null) ||
      !(row.failureCode === null || has(failureCodes, row.failureCode)) ||
      !(row.admissionReason === null || has(reasons, row.admissionReason)) ||
      typeof row.cleanupConfirmed !== 'boolean' ||
      typeof row.retained !== 'boolean'
    )
      return null;
    const readiness = row.readiness === null ? null : readinessDiagnostic(row.readiness);
    if (row.readiness !== null && readiness === null) return null;
    if ((row.admissionReason === null) !== (readiness === null)) return null;
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
      currentMode: row.currentMode,
      completedCaseCount: row.completedCaseCount,
      operation: row.operation,
      rootHttpStatus: row.rootHttpStatus,
      failureCode: row.failureCode,
      admissionReason: row.admissionReason,
      readiness,
      cleanupConfirmed: row.cleanupConfirmed,
      retained: row.retained,
    };
  } catch {
    return null;
  }
}

// Read only an own data field and complete TAP lines; a prefix is never success.
const typedArray = Object.getPrototypeOf(Uint8Array.prototype);
const viewBytes = Object.getOwnPropertyDescriptor(typedArray, 'byteLength')!.get!;
const viewBuffer = Object.getOwnPropertyDescriptor(typedArray, 'buffer')!.get!;
const viewOffset = Object.getOwnPropertyDescriptor(typedArray, 'byteOffset')!.get!;
const decoder = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true });
function completedControls(script: string, error: object) {
  try {
    const field = Object.getOwnPropertyDescriptor(error, 'stdout');
    if (!field || !Object.hasOwn(field, 'value')) return null;
    const value: unknown = field.value;
    let text: string;
    if (typeof value === 'string') {
      if (Buffer.byteLength(value, 'utf8') > 65536) return null;
      text = value;
    } else if (Buffer.isBuffer(value)) {
      const bytes: number = Reflect.apply(viewBytes, value, []);
      if (bytes > 65536) return null;
      // Fresh intrinsic view avoids any instance toString/length/byteLength getter.
      text = decoder.decode(
        new Uint8Array(
          Reflect.apply(viewBuffer, value, []),
          Reflect.apply(viewOffset, value, []),
          bytes,
        ),
      );
    } else return null;
    const maximum = scriptCaps[script];
    if (maximum === undefined) return null;
    let count = 0;
    // A timeout may cut a status row midway. Its unterminated tail earns no count.
    for (const line of text.split(/\r?\n/).slice(0, -1)) {
      if (line.startsWith('not ok ')) break;
      if (!line.startsWith('ok ')) continue;
      const passed = /^ok ([1-9][0-9]*) -(?: |$)/.exec(line);
      if (!passed) return null;
      const ordinal = Number(passed[1]);
      if (!Number.isSafeInteger(ordinal) || ordinal !== count + 1 || ordinal > maximum) return null;
      count++;
    }
    return count;
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
      completedControls: completedControls(script, error),
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
