import type { ChildProcess } from 'node:child_process';

export function bffDevToolsFailureStage(error: unknown): string;
export function bffProofCopy(locale: string, key: string): string;
export function bffPageKind(
  path: unknown,
): 'studio' | 'portal' | 'members' | 'member' | 'roles' | 'role' | 'profile' | null;
export function assertBffCookieAttributes(headers: readonly string[] | undefined): void;
export function assertBffPrivateOutput(scanners: readonly { readonly leaked: boolean }[]): void;
export function createBffCookieScanner(canaries: string[]): {
  push(chunk: string | Buffer): void;
  contains(value: string): boolean;
  finish(): void;
  readonly leaked: boolean;
  readonly tail: string;
};
export function withProofWaiters<T extends readonly unknown[]>(
  waiters: { readonly [K in keyof T]: Promise<T[K]> },
  action: () => unknown | Promise<unknown>,
): Promise<T>;
export function rememberBffCookies(
  headers: readonly string[] | undefined,
  canaries: string[],
): void;

export function createBffBrowserCleanup(
  resources: () => {
    browser?: {
      newBrowserCDPSession(): Promise<{ send(method: 'Browser.close'): Promise<unknown> }>;
      close(): Promise<void>;
    };
    browserProcess?: ChildProcess;
    nativeServer?: {
      process: ChildProcess;
      completed: Promise<{ code: number | null; signal: NodeJS.Signals | null }>;
    };
    owner: { dispose(): Promise<void> };
  },
): {
  run(): Promise<void>;
  readonly failureStage: string | null;
};
