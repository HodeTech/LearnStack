// @vitest-environment node
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { expect, test } from 'vitest';

import { closedFrameworkProofSync } from './framework-proof-closed-boundary';

test('copied transport source and observation controls pass every causal case', () =>
  closedFrameworkProofSync(() => {
    const frontend = fileURLToPath(new URL('../../../../', import.meta.url));
    const script = join(frontend, 'scripts/framework-transport-proof.test.mjs');
    const output = execFileSync(process.execPath, ['--test', script], {
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'pipe'],
      timeout: 10_000,
      maxBuffer: 65536,
      env: { PATH: process.env.PATH, NODE_ENV: 'production', NEXT_TELEMETRY_DISABLED: '1' },
    });
    for (const [field, count] of [
      ['tests', 11],
      ['pass', 11],
      ['fail', 0],
      ['cancelled', 0],
      ['skipped', 0],
      ['todo', 0],
    ] as const)
      expect(output).toMatch(new RegExp(`^# ${field} ${count}$`, 'm'));
  }));

test(
  'actual copied UI joins conditional gzip and deflate refusals before family release',
  () =>
    closedFrameworkProofSync(() => {
      const app = fileURLToPath(new URL('../../', import.meta.url));
      const script = join(app, 'scripts/verify-framework-transport.mjs');
      const output = execFileSync(process.execPath, [script], {
        encoding: 'utf8',
        timeout: 150_000,
        maxBuffer: 131072,
        env: {
          PATH: process.env.PATH,
          NODE_ENV: 'production',
          NODE_TLS_REJECT_UNAUTHORIZED: '1',
          NEXT_TELEMETRY_DISABLED: '1',
        },
        stdio: ['ignore', 'pipe', 'pipe'],
      });
      const receipt = JSON.parse(output) as {
        cases: Array<{
          case: string;
          status: number;
          events: Array<{ kind: string; value: string | null }>;
        }>;
        familyRetired: boolean;
        privateCleanup: boolean;
      };
      expect(Object.keys(receipt).sort()).toEqual(['cases', 'familyRetired', 'privateCleanup']);
      expect(receipt.familyRetired).toBe(true);
      expect(receipt.privateCleanup).toBe(true);
      expect(receipt.cases.map((row) => row.case)).toEqual([
        'healthy-identity',
        'healthy-gzip',
        'healthy-deflate',
        'fence-gzip',
        'fence-deflate',
      ]);
      for (const row of receipt.cases) {
        expect(Object.keys(row).sort()).toEqual(['case', 'events', 'status']);
        expect(row.status).toBe(row.case.startsWith('healthy-') ? 200 : 503);
        const required =
          row.case === 'healthy-identity'
            ? ['router', 'none', 'response-finish', 'response-close', 'handler-ok']
            : [
                'router',
                'allocation',
                'encoder-close',
                'response-finish',
                'response-close',
                'handler-ok',
              ];
        expect(row.events.map((event) => event.kind).sort()).toEqual(required.sort());
        expect(row.events.findIndex((event) => event.kind === 'response-close')).toBeGreaterThan(
          row.events.findIndex((event) => event.kind === 'response-finish'),
        );
        for (const event of row.events) {
          expect(Object.keys(event).sort()).toEqual(['kind', 'value']);
          if (event.kind === 'none') expect(event.value).toBe('not acceptable');
          else if (event.kind === 'allocation')
            expect(event.value).toBe(row.case.endsWith('gzip') ? 'gzip' : 'deflate');
          else if (event.kind === 'encoder-close')
            expect(event.value).toBe(row.case.startsWith('healthy-') ? 'ended' : 'unended');
          else expect(event.value).toBeNull();
        }
      }
    }),
  160_000,
);
