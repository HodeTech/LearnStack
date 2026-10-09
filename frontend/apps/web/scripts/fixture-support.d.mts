import type { ChildProcess } from 'node:child_process';
import type { EventEmitter } from 'node:events';
import type { Server } from 'node:net';

export function createPrivateScanner(secrets?: string[]): {
  push(chunk: string | Buffer): void;
  contains(value: string): boolean;
  finish(): void;
  readonly leaked: boolean;
  readonly tail: string;
};
export function fixtureEnvironment(options: {
  root: string;
  nodeEnv?: string;
  nodePath?: string;
  privateValues?: Record<string, string>;
  debug?: string;
}): NodeJS.ProcessEnv;
export function createFixtureOwner(options?: { exit?: (code: number) => void }): {
  ownRoot(path: string): string;
  ownChild<T extends ChildProcess>(child: T): T;
  ownServer<T extends Server>(server: T): T;
  assertActive(): void;
  dispose(): Promise<void>;
  cancel(): Promise<void>;
  install(options?: { control?: EventEmitter; event?: 'close' | 'disconnect' }): void;
};
