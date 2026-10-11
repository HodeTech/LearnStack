export interface CompletionState {
  total: number;
  statuses: Record<string, number>;
  paths: Record<string, number>;
  malformed: boolean;
  pendingBytes: number;
}
export function apiCompletionObserver(): {
  state: CompletionState;
  consume(chunk: Buffer, stream?: 'stdout' | 'stderr'): void;
  finish(): void;
};
export function finalCompletionCensus(
  beforeCleanup: CompletionState,
  afterCleanup: CompletionState,
  expected: number,
): void;
export function completionDelta(
  before: CompletionState,
  after: CompletionState,
  expected: number,
): {
  count: number;
  statuses: Record<string, number>;
};
