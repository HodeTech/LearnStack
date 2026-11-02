// @vitest-environment node
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { expect, it } from 'vitest';

import { closedFrameworkProofSync } from './framework-proof-closed-boundary';

it(
  'joins owned setup groups when reusable-worker controls are present or omitted',
  () =>
    closedFrameworkProofSync(() => {
      const repository = fileURLToPath(new URL('../../../../../', import.meta.url));
      const output = execFileSync(
        process.execPath,
        ['--test', join(repository, 'scripts/demo-dotnet-owner.test.mjs')],
        {
          encoding: 'utf8',
          stdio: ['ignore', 'pipe', 'pipe'],
          timeout: 60_000,
          maxBuffer: 65_536,
          env: {
            PATH: process.env.PATH,
            HOME: process.env.HOME,
            TMPDIR: process.env.TMPDIR,
            NODE_ENV: 'test',
          },
        },
      );
      for (const [field, count] of [
        ['tests', 5],
        ['pass', process.platform === 'win32' ? 0 : 5],
        ['fail', 0],
        ['cancelled', 0],
        ['skipped', process.platform === 'win32' ? 5 : 0],
        ['todo', 0],
      ] as const)
        expect(output).toMatch(new RegExp(`^# ${field} ${count}$`, 'm'));
    }),
  65_000,
);
