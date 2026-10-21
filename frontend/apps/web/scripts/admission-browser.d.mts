type NavigationEvent = {
  readonly url: string;
  readonly type: string;
  readonly rsc: boolean;
  readonly status: number | null;
};
export function assertFlightFallback(
  events: readonly NavigationEvent[],
  origin: string,
  target: string,
  status: number,
): void;
export function verifyBrowserFallback(options: {
  readonly owner: ReturnType<typeof createFixtureOwner>;
  readonly app: string;
  readonly root: string;
  readonly certificate: string | Buffer;
  readonly origin: string;
  readonly initialPath: string;
  readonly target: string;
  readonly status: number;
  readonly beforeNavigation: () => Promise<void>;
  readonly afterNavigation: () => Promise<void>;
  readonly secrets: readonly string[];
}): Promise<ReturnType<typeof createPrivateScanner>>;
import type { createFixtureOwner, createPrivateScanner } from './fixture-support.mjs';
export type BrowserPhase =
  | 'browser-launch'
  | 'browser-devtools'
  | 'browser-discovery'
  | 'browser-protocol'
  | 'browser-navigation'
  | 'browser-hydration'
  | 'browser-arm'
  | 'browser-click'
  | 'browser-fallback'
  | 'browser-wire'
  | 'browser-completion'
  | 'browser-private-carrier'
  | 'browser-shutdown';
export function browserFailureDetail(error: unknown): string;
export function atBrowserPhase<T>(phase: BrowserPhase, operation: () => T | Promise<T>): Promise<T>;
export function waitForBrowserDevTools(options: {
  readonly assertActive: () => void;
  readonly isLive: () => boolean;
  readonly readPort: () => number | null;
}): Promise<number>;
export function waitForBrowserPage(options: {
  readonly assertActive: () => void;
  readonly isLive: () => boolean;
  readonly port: number;
}): Promise<URL>;
export function withBrowserCleanup<T>(
  operation: () => T | Promise<T>,
  cleanup: () => void | Promise<void>,
): Promise<T>;
