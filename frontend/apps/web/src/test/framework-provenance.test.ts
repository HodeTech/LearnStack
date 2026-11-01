// @vitest-environment node
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

import { expect, test } from 'vitest';

test('qualified framework provenance refuses drift and preserves owned boundaries', () => {
  const script = fileURLToPath(
    new URL('../../../../scripts/test-framework-provenance.mjs', import.meta.url),
  );
  const result = execFileSync(process.execPath, ['--test', script], {
    cwd: fileURLToPath(new URL('../../../../', import.meta.url)),
    encoding: 'utf8',
    timeout: 30_000,
    maxBuffer: 1024 * 1024,
    env: {
      PATH: process.env.PATH,
      NODE_ENV: 'production',
      NEXT_TELEMETRY_DISABLED: '1',
    },
  });
  expect(result).toMatch(/# tests 93\b/);
  expect(result).toMatch(/# pass 93\b/);
  expect(result).toMatch(/# fail 0\b/);
  expect(result).toMatch(/# cancelled 0\b/);
  expect(result).toMatch(/# skipped 0\b/);
}, 35_000);
