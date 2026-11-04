// @vitest-environment node
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

import { expect, test } from 'vitest';

const frontend = fileURLToPath(new URL('../../../../', import.meta.url));
const environment: NodeJS.ProcessEnv = {
  PATH: process.env.PATH,
  NODE_ENV: 'production',
  NEXT_TELEMETRY_DISABLED: '1',
};

test('runtime family controls join actual children and retain shutdown failures', () => {
  const result = execFileSync(
    process.execPath,
    ['--test', 'scripts/framework-runtime-family.test.mjs'],
    {
      cwd: frontend,
      encoding: 'utf8',
      timeout: 30_000,
      maxBuffer: 1024 * 1024,
      env: environment,
    },
  );
  const count = result.match(/^# tests (\d+)$/m)?.[1];
  expect(count).toBe('25');
  expect(result).toMatch(new RegExp(`^# pass ${count}$`, 'm'));
  expect(result).toMatch(/^# fail 0$/m);
  expect(result).toMatch(/^# cancelled 0$/m);
  expect(result).toMatch(/^# skipped 0$/m);
  expect(result).toMatch(/^# todo 0$/m);
}, 35_000);

test('native startup control keeps held setup and actual TLS ownership joined', () => {
  const result = execFileSync(process.execPath, ['scripts/public-runtime-control.test.mjs'], {
    cwd: frontend,
    encoding: 'utf8',
    timeout: 10_000,
    maxBuffer: 1024 * 1024,
    env: environment,
  });
  expect(result).toContain(
    'Native runtime control checks passed (inert Next/dependency fixtures; actual TLS listeners).',
  );
}, 15_000);
