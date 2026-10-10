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
