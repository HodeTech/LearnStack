// @vitest-environment node
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

import { expect, test } from 'vitest';

import {
  emitProvenanceCountDiagnostic,
  emitProvenanceDiagnostic,
} from './framework-finite-diagnostic';
import { closedFrameworkProofSync } from './framework-proof-closed-boundary';

test(
  'qualified framework provenance refuses drift and preserves owned boundaries',
  () =>
    closedFrameworkProofSync(() => {
      const script = fileURLToPath(
        new URL('../../../../scripts/test-framework-provenance.mjs', import.meta.url),
      );
      const started = performance.now();
      let result: string;
      try {
        result = execFileSync(process.execPath, ['--test', script], {
          cwd: fileURLToPath(new URL('../../../../', import.meta.url)),
          encoding: 'utf8',
          timeout: 90_000,
          maxBuffer: 1024 * 1024,
          stdio: ['ignore', 'pipe', 'pipe'],
          env: {
            PATH: process.env.PATH,
            NODE_ENV: 'production',
            NEXT_TELEMETRY_DISABLED: '1',
          },
        });
      } catch (error) {
        emitProvenanceDiagnostic(error, performance.now() - started);
        throw error;
      }
      for (const [counter, value] of [
        ['tests', 112],
        ['pass', 112],
        ['fail', 0],
        ['cancelled', 0],
        ['skipped', 0],
      ] as const) {
        try {
          expect(result).toMatch(new RegExp('# ' + counter + ' ' + value + '\\b'));
        } catch (error) {
          emitProvenanceCountDiagnostic(counter, performance.now() - started);
          throw error;
        }
      }
    }),
  100_000,
);
