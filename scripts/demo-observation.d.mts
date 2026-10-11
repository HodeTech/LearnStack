export interface CompletionState {
  total: number;
  statuses: Record<string, number>;
  paths: Record<string, number>;
  malformed: boolean;
}
export function apiCompletionObserver(): {
  state: CompletionState;
  consume(chunk: Buffer): void;
};
export function completionDelta(
  before: CompletionState,
  after: CompletionState,
  expected: number,
): {
  count: number;
  statuses: Record<string, number>;
};
