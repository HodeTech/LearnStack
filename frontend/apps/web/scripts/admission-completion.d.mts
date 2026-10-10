import type { ServerResponse } from 'node:http';

export function createNativeCompletionObserver(): Readonly<{
  track(response: Pick<ServerResponse, 'once' | 'off'>): void;
  settled(): Promise<void>;
}>;
