// @vitest-environment node
import { execFileSync } from 'node:child_process';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

describe('Bff_Credentials_And_Projections_Are_Confined', () => {
  beforeEach(() => {
    process.stdout.write('BFF development proof entered: counter\n');
  });
  afterEach(() => {
    process.stdout.write('BFF development proof finished: counter\n');
  });
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
