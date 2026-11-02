// @vitest-environment node
import { expect, test } from 'vitest';

type ColdLintResult = {
  commandsSucceeded: boolean;
  cachePresent: boolean;
  guardResult: string;
  sourcesUnchanged: boolean;
  familiesRetired: boolean;
  leasesRetired: boolean;
  rootDisposed: boolean;
};

async function check(cached: boolean): Promise<ColdLintResult> {
  const helper = new URL('../scripts/test-cold-lint-output.mjs', import.meta.url).href;
  const module = (await import(/* @vite-ignore */ helper)) as {
    checkColdLintOutput: (cached: boolean) => Promise<ColdLintResult>;
  };
  return module.checkColdLintOutput(cached);
}

test.each([
  [false, 'accepted'],
  [true, 'output_unowned'],
] as const)(
  'cold typecheck and lint with cache=%s produces guarded output',
  async (cached, guardResult) => {
    expect(await check(cached)).toEqual({
      commandsSucceeded: true,
      cachePresent: cached,
      guardResult,
      sourcesUnchanged: true,
      familiesRetired: true,
      leasesRetired: true,
      rootDisposed: true,
    });
  },
  90_000,
);
