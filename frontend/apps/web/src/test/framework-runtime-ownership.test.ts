// @vitest-environment node
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import { closedFrameworkProofSync } from './framework-proof-closed-boundary';
import { registerRouterFaultCases } from './framework-router-faults.cases';
import { runFrameworkOwnershipGroup } from './framework-runtime-ownership';
import { registerRuntimeRecoveryCases } from './framework-runtime-recovery.cases';
import { registerRuntimeWorkerCases } from './framework-runtime-workers.cases';

const environment: NodeJS.ProcessEnv = {
  PATH: process.env.PATH,
  NODE_ENV: 'production',
  NODE_TLS_REJECT_UNAUTHORIZED: '1',
  NEXT_TELEMETRY_DISABLED: '1',
};

describe.sequential('actual initialized Next process ownership', () => {
  it(
    'consumes real startup stops and retains unauthenticated pre-HELLO evidence',
    () =>
      closedFrameworkProofSync(() => {
        runFrameworkOwnershipGroup('startup');
      }),
    160_000,
  );

  it.each([
    ['framework-ownership-proof-source.test.mjs', 1],
    ['framework-private-source.test.mjs', 3],
    ['framework-worker-watchdog-source.test.mjs', 5],
  ] as const)('executes every closed control in %s', (name, count) =>
    closedFrameworkProofSync(() => {
      const frontend = fileURLToPath(new URL('../../../../', import.meta.url));
      const script = join(frontend, 'scripts', name);
      const output = execFileSync(process.execPath, ['--test', script], {
        encoding: 'utf8',
        stdio: ['ignore', 'pipe', 'pipe'],
        timeout: 10_000,
        maxBuffer: 65536,
        env: environment,
      });
      for (const [field, expected] of [
        ['tests', count],
        ['pass', count],
        ['fail', 0],
        ['cancelled', 0],
        ['skipped', 0],
        ['todo', 0],
      ] as const)
        expect(output).toMatch(new RegExp(`^# ${field} ${expected}$`, 'm'));
    }),
  );
  registerRuntimeWorkerCases();
  registerRuntimeRecoveryCases();
  registerRouterFaultCases();
});
