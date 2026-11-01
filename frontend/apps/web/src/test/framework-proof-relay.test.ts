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

test.each([
  ['apps/web/scripts/test-framework-proof-relay.mjs', 18],
  ['scripts/framework-proof-host-broker.test.mjs', 7],
  ['apps/web/scripts/test-production-runtime-owner-stop.mjs', 6],
  ['apps/web/scripts/test-framework-proof-source.mjs', 9],
] as const)(
  'copied proof runner %s passes every closed-schema control',
  (script, count) => {
    const result = execFileSync(process.execPath, ['--test', script], {
      cwd: frontend,
      encoding: 'utf8',
      timeout: 10_000,
      maxBuffer: 1024 * 1024,
      env: environment,
    });
    expect(result).toMatch(new RegExp(`^# tests ${count}$`, 'm'));
    expect(result).toMatch(new RegExp(`^# pass ${count}$`, 'm'));
    expect(result).toMatch(/^# fail 0$/m);
    expect(result).toMatch(/^# cancelled 0$/m);
    expect(result).toMatch(/^# skipped 0$/m);
    expect(result).toMatch(/^# todo 0$/m);
  },
  15_000,
);
