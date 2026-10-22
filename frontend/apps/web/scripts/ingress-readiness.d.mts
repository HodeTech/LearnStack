import type { ChildProcess } from 'node:child_process';

export function isReadinessNotification(message: unknown): boolean;

export function boundNextVersionFetch(
  delegate: typeof fetch,
  notify: (observation: { state: string; errorKind: string; elapsedMilliseconds: number }) => void,
): typeof fetch;

export function createIngressReadiness(child: ChildProcess): {
  output(chunk: string | Buffer): void;
  wait(options: {
    port: number;
    probe: () => Promise<{ status?: number; body: string }>;
    assertActive: () => void;
  }): Promise<Record<string, string | number | boolean | null>>;
};
