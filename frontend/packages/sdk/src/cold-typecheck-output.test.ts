// @vitest-environment node
import { expect, test } from 'vitest';

type ColdResult = {
  compilerSucceeded: boolean;
  buildInfoPresent: boolean;
  guardResult: string;
  sourcesUnchanged: boolean;
  leasesRetired: boolean;
  rootDisposed: boolean;
};

async function check(incremental: boolean): Promise<ColdResult> {
  const helper = new URL('../scripts/test-cold-typecheck-output.mjs', import.meta.url).href;
  const module = (await import(/* @vite-ignore */ helper)) as {
    checkColdTypecheckOutput: (incremental: boolean) => Promise<ColdResult>;
  };
  return module.checkColdTypecheckOutput(incremental);
}

test.each([
  [false, 'accepted'],
  [true, 'output_unowned'],
] as const)(
  'cold typecheck with incremental=%s produces guarded output',
  async (incremental, guardResult) => {
    expect(await check(incremental)).toEqual({
      compilerSucceeded: true,
      buildInfoPresent: incremental,
      guardResult,
      sourcesUnchanged: true,
      leasesRetired: true,
      rootDisposed: true,
    });
  },
  90_000,
);
