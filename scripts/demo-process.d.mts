import type { ChildProcess } from 'node:child_process';
export function installDemoCancellation(cancel: () => void): () => void;
export class DemoCommandError extends Error {
  constructor(label: string, cause?: unknown);
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
  exit: Promise<{ code: number }>;
  result(): { output: string; failed?: Error };
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
