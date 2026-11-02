// @vitest-environment node
import { expect, test } from 'vitest';

type StopResult = {
  signalObserved: boolean;
  signalReceived: boolean;
  commandDispatchAttempted: boolean;
  missingForwardingRefused: boolean;
  familyRetired: boolean;
  leasesRetired: boolean;
  sourcesUnchanged: boolean;
  rootDisposed: boolean;
};

async function check(signal: 'SIGTERM' | 'SIGINT'): Promise<StopResult> {
  const helper = new URL('../scripts/test-producer-cli-stop.mjs', import.meta.url).href;
  const module = (await import(/* @vite-ignore */ helper)) as {
    checkProducerCliStop: (signal: 'SIGTERM' | 'SIGINT') => Promise<StopResult>;
  };
  return module.checkProducerCliStop(signal);
}

test.each(['SIGTERM', 'SIGINT'] as const)(
  'producer CLI releases its actual leases after %s before the first child family',
  async (signal) => {
    expect(await check(signal)).toEqual({
      signalObserved: true,
      signalReceived: true,
      commandDispatchAttempted: false,
      missingForwardingRefused: true,
      familyRetired: true,
      leasesRetired: true,
      sourcesUnchanged: true,
      rootDisposed: true,
    });
  },
  90_000,
);
