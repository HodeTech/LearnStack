export class PublicDemoFailure extends Error {
  constructor(reason: unknown);
  reason: string;
}

export interface PublicDemoFailureRecord {
  stage: string;
  reason: string;
  mode: string | null;
  lane: number | null;
  kind: string | null;
  run: number | null;
  workerStage: string | null;
  exitCode: number | null;
  signal: string | null;
}

export function publicDemoWorkerStage(stage: unknown): string | null;
export function publicDemoChildExited(child: {
  exitCode: number | null;
  signalCode: string | null;
}): boolean;
export function publicDemoFailure(
  stage: unknown,
  error: unknown,
  input?: Record<string, unknown>,
  child?: Record<string, unknown>,
): PublicDemoFailureRecord;
export function publicDemoOperation<T>(options: {
  input?: Record<string, unknown>;
  run: (stage: (value: string) => void) => Promise<T>;
  cleanup: () => Promise<unknown>;
  child: () => Record<string, unknown> | undefined;
  record: (failure: PublicDemoFailureRecord) => void;
}): Promise<T>;
