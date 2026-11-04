// @vitest-environment node
import { execFileSync } from 'node:child_process';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

describe('Bff_Credentials_And_Projections_Are_Confined', () => {
  beforeEach(() => {
    process.stdout.write('BFF development proof entered: positive\n');
  });
  afterEach(() => {
    process.stdout.write('BFF development proof finished: positive\n');
  });
  it('development callback/logout canaries stay out of actual console and persisted framework traces', () => {
    const script = new URL('../../scripts/verify-bff-protocol.mjs', import.meta.url);
    expect(
      execFileSync(process.execPath, [script.pathname, '--dev'], {
        encoding: 'utf8',
        timeout: 150_000,
        maxBuffer: 65536,
      }),
    ).toBe('BFF development diagnostics: passed\n');
  }, 160_000);
});
