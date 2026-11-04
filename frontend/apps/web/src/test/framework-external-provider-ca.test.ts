// @vitest-environment node
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { expect, test } from 'vitest';

import { closedFrameworkProofSync } from './framework-proof-closed-boundary';

test(
  'copied external provider CA stays bound to its owned Node startup',
  () =>
    closedFrameworkProofSync(() => {
      const frontend = fileURLToPath(new URL('../../../../', import.meta.url));
      const output = execFileSync(
        process.execPath,
        ['--test', join(frontend, 'scripts/framework-external-provider-ca.test.mjs')],
        {
          cwd: frontend,
          encoding: 'utf8',
          stdio: ['ignore', 'pipe', 'pipe'],
          timeout: 20_000,
          maxBuffer: 65_536,
          env: {
            PATH: process.env.PATH,
            NODE_ENV: 'production',
            NODE_TLS_REJECT_UNAUTHORIZED: '1',
          },
        },
      );
      for (const [field, expected] of [
        ['tests', 3],
        ['pass', 3],
        ['fail', 0],
        ['cancelled', 0],
        ['skipped', 0],
        ['todo', 0],
      ] as const)
        expect(output).toMatch(new RegExp(`^# ${field} ${expected}$`, 'm'));
    }),
  25_000,
);
