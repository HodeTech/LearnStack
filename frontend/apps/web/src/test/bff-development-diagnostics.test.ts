// @vitest-environment node
import { execFileSync } from 'node:child_process';

import { describe, expect, it } from 'vitest';

describe('Bff_Credentials_And_Projections_Are_Confined', () => {
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
  it('the same development canary kills removal of native trace suppression even with logging false', () => {
    const script = new URL('../../scripts/verify-bff-protocol.mjs', import.meta.url);
    let failure: unknown;
    try {
      execFileSync(process.execPath, [script.pathname, '--without-native-diagnostics'], {
        encoding: 'utf8',
        timeout: 150_000,
        maxBuffer: 65536,
        stdio: ['ignore', 'pipe', 'pipe'],
      });
    } catch (error) {
      failure = error;
    }
    expect(failure).toMatchObject({
      status: 1,
      signal: null,
      stdout: '',
      stderr: 'BFF native Next protocol failed: dev-trace\n',
    });
  }, 160_000);
});
