import type { ChildProcess } from 'node:child_process';

export function stopTestChild(child: ChildProcess): Promise<Readonly<{ forced: boolean }>>;
/** Negative cleanup gate; it does not prove another compiler family retired. */
export function assertUnforcedTestChildStop(
  child: ChildProcess,
  stopped: Readonly<{ forced: boolean }>,
): void;
