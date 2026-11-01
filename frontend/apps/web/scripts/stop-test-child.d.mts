import type { ChildProcess } from 'node:child_process';

export type RuntimeTestFamily = Readonly<{
  pid: number;
  group: number;
  parentPid: number;
  leaseNonce: string;
  incarnation: string;
}>;
export type RuntimeTestLease = Readonly<{
  format: 1;
  pid: number;
  nonce: string;
  kind: 'production' | 'development';
}>;
export type RuntimeTestProducerFamily = Readonly<{
  pid: number;
  group: number;
  parentPid: number;
  leaseNonce: string;
}>;

export function markRuntimeTestChild(child: ChildProcess): Readonly<{
  captureLease(record: RuntimeTestLease): RuntimeTestLease;
  captureProducerFamily(handle: RuntimeTestProducerFamily): RuntimeTestProducerFamily;
  captureFamily(handle: RuntimeTestFamily): RuntimeTestFamily;
  lease(): RuntimeTestLease | null;
  producers(): readonly RuntimeTestProducerFamily[];
  family(): RuntimeTestFamily | null;
  retired(): boolean;
}>;

export type TestChildStop = Readonly<{
  forced: boolean;
  ownerCode?: number | null;
  ownerSignal?: NodeJS.Signals | null;
}>;
export function stopTestChild(child: ChildProcess): Promise<TestChildStop>;
/** Clean owner zero is delegated evidence of its private runtime family's physical join. */
export function stopProductionRuntimeOwner(
  child: ChildProcess,
): Promise<Readonly<{ forced: false }>>;
/** Negative cleanup gate; it does not prove another compiler family retired. */
export function assertUnforcedTestChildStop(child: ChildProcess, stopped: TestChildStop): void;
