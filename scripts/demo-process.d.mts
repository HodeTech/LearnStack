import type { ChildProcess } from 'node:child_process';
export function installDemoCancellation(cancel: () => void): () => void;
export class DemoCommandError extends Error {
  constructor(
    label: string,
    cause?: unknown,
    status?: { code: number | null; signal: string | null },
    outcome?: {
      reason?: 'exit' | 'deadline' | 'cancelled' | 'spawn' | 'output' | 'cleanup' | 'unknown';
      origin?: 'command' | 'daemon';
    },
  );
  readonly reason: 'exit' | 'deadline' | 'cancelled' | 'spawn' | 'output' | 'cleanup' | 'unknown';
  readonly origin: 'command' | 'daemon';
  readonly exitCode: number | null;
  readonly signalCode: string | null;
}
interface Options {
  cwd?: string;
  env?: NodeJS.ProcessEnv;
  label?: string;
  capture?: boolean;
  timeout?: number;
  observe?: (chunk: Buffer, stream: 'stdout' | 'stderr') => void;
}
interface Running {
  child: ChildProcess;
  /** Native failures publish at exit; success retains complete output through close. */
  exit: Promise<{ code: number | null; signal: string | null }>;
  refusal: Promise<{ cause: unknown; reason: 'spawn' | 'output' }>;
  result(): { output: string; failed?: Error; failureReason?: 'spawn' | 'output' };
}
export class DemoProcesses {
  constructor(signal: AbortSignal);
  failure: Promise<Error>;
  assertActive(): void;
  start(command: string, args: string[], options?: Options): Running;
  daemon(command: string, args: string[], options: Options): Running;
  run(command: string, args: string[], options?: Options): Promise<string>;
  dispose(): Promise<void>;
}
