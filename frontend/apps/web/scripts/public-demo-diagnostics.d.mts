export class PublicDemoFailure extends Error {
  constructor(reason: unknown, cause?: unknown);
  reason: string;
}

export interface PublicDemoFailureRecord {
  stage: string;
  reason: string;
  process: 'worker' | 'browser' | 'daemon' | null;
  mode: string | null;
  lane: number | null;
  kind: string | null;
  run: number | null;
  workerStage: string | null;
  exitCode: number | null;
  signal: string | null;
}

export type PublicDemoChildStatus = {
  exitCode?: unknown;
  signalCode?: unknown;
};

export function publicDemoWorkerStage(stage: unknown): string | null;
export function publicDemoWorkerOutput(chunk: Buffer): string[];
export function publicDemoChildExited(child: {
  exitCode: number | null;
  signalCode: string | null;
}): boolean;
export function waitForPublicDemoBrowserPort(options: {
  readonly assertActive: () => void;
  readonly readPort: () => unknown;
  readonly signal?: AbortSignal;
  readonly timeout?: number;
}): Promise<number>;
export function publicDemoFailure(
  stage: unknown,
  error: unknown,
  input?: Record<string, unknown>,
  child?: PublicDemoChildStatus,
  signal?: AbortSignal,
): PublicDemoFailureRecord;
export function publicDemoOperation<T>(options: {
  input?: Record<string, unknown>;
  signal?: AbortSignal;
  run: (stage: (value: string) => void) => Promise<T>;
  cleanup: () => Promise<unknown>;
  child: () => PublicDemoChildStatus | undefined;
  record: (failure: PublicDemoFailureRecord) => void;
}): Promise<T>;
