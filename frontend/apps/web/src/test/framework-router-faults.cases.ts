// @vitest-environment node
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { expect, test } from 'vitest';

import { emitRouterCountDiagnostic, emitRouterDiagnostic } from './framework-finite-diagnostic';
import { closedFrameworkProofSync } from './framework-proof-closed-boundary';

const frontend = fileURLToPath(new URL('../../../../', import.meta.url));
const app = join(frontend, 'apps/web');
const names = [
  'pipe-before-selection',
  'abort-before-selection',
  'source-fault-gzip',
  'encoder-early-fault-gzip',
  'source-cleanup-fault-gzip',
  'end-work-reject-gzip',
  'finish-cleanup-fault-gzip',
  'constructor-failure-gzip',
];
const object = (value: unknown): Record<string, unknown> => {
  if (!value || typeof value !== 'object' || Array.isArray(value))
    throw new Error('Fault receipt shape refused');
  return value as Record<string, unknown>;
};
const keys = (value: Record<string, unknown>, expected: string[]) =>
  expect(Object.keys(value).sort()).toEqual([...expected].sort());

export function registerRouterFaultCases() {
  test(
    'copied initialized-router faults preserve native failure through actual fixture cleanup',
    () =>
      closedFrameworkProofSync(() => {
        const output = execFileSync(
          process.execPath,
          [join(app, 'scripts/verify-framework-router-faults.mjs')],
          {
            encoding: 'utf8',
            timeout: 150_000,
            maxBuffer: 131_072,
            env: {
              PATH: process.env.PATH,
              NODE_ENV: 'production',
              NODE_TLS_REJECT_UNAUTHORIZED: '1',
              NEXT_TELEMETRY_DISABLED: '1',
            },
            stdio: ['ignore', 'pipe', 'pipe'],
          },
        );
        const receipt = object(JSON.parse(output));
        keys(receipt, [
          'version',
          'cases',
          'observations',
          'manifestSha256',
          'inventorySha256',
          'pipeEntries',
          'registrationNegatives',
          'expectedChildFailure',
          'native',
          'stop',
          'familyRetired',
          'reservationsReleased',
          'privateCleanup',
          'constructionUnconfirmedAtExit',
        ]);
        expect(receipt.version).toBe(1);
        expect(receipt.pipeEntries).toBe(8);
        expect(receipt.registrationNegatives).toBe(3);
        expect(receipt.expectedChildFailure).toBe(true);
        expect(receipt.familyRetired).toBe(true);
        expect(receipt.reservationsReleased).toBe(true);
        expect(receipt.privateCleanup).toBe(true);
        expect(receipt.constructionUnconfirmedAtExit).toBe(true);
        expect(receipt.native).toEqual({
          code: 1,
          signal: null,
          handshakeValidated: true,
          listenerReady: true,
          requestedStop: true,
          startupCancelled: false,
          parentLost: false,
          fault: 'channel-fault',
          directChildClosed: true,
          groupAbsent: true,
        });
        expect(receipt.stop).toEqual({ forced: false, ownerCode: 1, ownerSignal: null });
        for (const name of ['manifestSha256', 'inventorySha256'])
          expect(receipt[name]).toMatch(/^[a-f0-9]{64}$/);
        if (!Array.isArray(receipt.cases) || !Array.isArray(receipt.observations))
          throw new Error('Fault receipt arrays refused');
        expect(receipt.cases).toHaveLength(8);
        for (const [index, value] of receipt.cases.entries()) {
          const row = object(value);
          keys(row, ['case', 'wire']);
          expect(row.case).toBe(names[index]);
          const wire = object(row.wire);
          keys(wire, [
            'status',
            'encoding',
            'responseCount',
            'ended',
            'abortRequested',
            'wireBytes',
          ]);
          expect([null, 200, 500, 503]).toContain(wire.status);
          expect([null, 'gzip']).toContain(wire.encoding);
          expect([0, 1]).toContain(wire.responseCount);
          expect(typeof wire.ended).toBe('boolean');
          expect(typeof wire.abortRequested).toBe('boolean');
          if (typeof wire.wireBytes !== 'number' || !Number.isSafeInteger(wire.wireBytes))
            throw new Error('Fault wire bound refused');
          expect(wire.wireBytes).toBeGreaterThanOrEqual(0);
          expect(wire.wireBytes).toBeLessThanOrEqual(65536);
          if (index === 6)
            expect(wire).toMatchObject({
              status: 200,
              encoding: 'gzip',
              responseCount: 1,
              ended: true,
              abortRequested: false,
            });
          if (index === 1)
            expect(wire).toMatchObject({
              status: null,
              responseCount: 0,
              ended: false,
              abortRequested: true,
              wireBytes: 0,
            });
        }
        expect(receipt.observations.length).toBeLessThanOrEqual(256);
        let pid: unknown;
        const counts = new Map<string, number>();
        for (const [index, value] of receipt.observations.entries()) {
          const row = object(value);
          keys(row, ['v', 'p', 'n', 'c', 'k', 'x']);
          expect(row.v).toBe(1);
          expect(row.n).toBe(index + 1);
          if (typeof row.p !== 'number' || !Number.isSafeInteger(row.p) || row.p <= 1)
            throw new Error('Fault runtime identity refused');
          pid ??= row.p;
          expect(row.p).toBe(pid);
          expect(names).toContain(row.c);
          if (typeof row.k !== 'string') throw new Error('Fault event kind refused');
          const basic = [
            'arm',
            'route',
            'pull',
            'pipe',
            'allocation',
            'source-fault',
            'encoder-fault',
            'end-bound',
            'end-reject',
            'aggregate-bound',
            'aggregate-rejected',
            'cleanup-fault',
            'constructor-fault',
            'response-finish',
            'response-close',
            'missing-refused',
            'duplicate-refused',
            'late-refused',
            'retired',
            'unconfirmed',
            'stop',
            'terminal-qualified',
            'watchdog',
          ];
          if (basic.includes(row.k)) expect(row.x).toBeNull();
          else if (['transition', 'observer-joined', 'encoder-close', 'handler'].includes(row.k))
            expect(['abort', 'failure', 'fulfilled', 'rejected', 'ended', 'unended']).toContain(
              row.x,
            );
          else if (row.k === 'join') {
            const data = object(row.x);
            keys(data, ['phase', 'index', 'scriptId', 'sha256', 'line', 'column']);
            expect(['pipe', 'router']).toContain(data.phase);
            expect(data.scriptId).toMatch(/^[0-9]{1,16}$/);
            expect(data.sha256).toMatch(/^[a-f0-9]{64}$/);
            for (const key of ['index', 'line', 'column'])
              if (typeof data[key] !== 'number' || !Number.isSafeInteger(data[key]))
                throw new Error('Fault join position refused');
          } else if (row.k === 'result') {
            const data = object(row.x);
            keys(data, ['phase', 'hasPrimary', 'hasEnd', 'hasCleanup']);
            expect(['pipe', 'router']).toContain(data.phase);
            for (const key of ['hasPrimary', 'hasEnd', 'hasCleanup'])
              expect(typeof data[key]).toBe('boolean');
          } else throw new Error('Fault event kind refused');
          counts.set(row.k, (counts.get(row.k) ?? 0) + 1);
        }
        for (const name of ['arm', 'route', 'pull', 'pipe', 'response-close'])
          expect(counts.get(name)).toBe(8);
        expect(counts.get('retired')).toBe(7);
        expect(counts.get('unconfirmed')).toBe(1);
        expect(counts.get('terminal-qualified')).toBe(1);
        expect(counts.get('watchdog')).toBe(1);
        for (const name of ['missing-refused', 'duplicate-refused', 'late-refused'])
          expect(counts.get(name)).toBe(1);
        const finalRows = receipt.observations.map(object).filter((row) => row.c === names[7]);
        for (const name of [
          'allocation',
          'encoder-close',
          'observer-joined',
          'handler',
          'retired',
          'result',
          'response-finish',
        ])
          expect(finalRows.some((row) => row.k === name)).toBe(false);
      }),
    160_000,
  );

  test(
    'closed router fault source, native factory and finite-evidence controls pass',
    () =>
      closedFrameworkProofSync(() => {
        for (const [script, count] of [
          ['framework-router-fault.test.mjs', 31],
          ['framework-router-fault-gate.test.mjs', 10],
          ['framework-router-registration.test.mjs', 4],
          ['framework-router-next-chain.test.mjs', 3],
          ['framework-router-fault-prefix.test.mjs', 13],
          ['framework-router-registration-capture.test.mjs', 9],
        ] as const) {
          const started = performance.now();
          let output: string;
          try {
            output = execFileSync(process.execPath, ['--test', join(frontend, 'scripts', script)], {
              encoding: 'utf8',
              timeout: 10_000,
              maxBuffer: 65536,
              env: { PATH: process.env.PATH, NODE_ENV: 'production' },
              stdio: ['ignore', 'pipe', 'pipe'],
            });
          } catch (error) {
            emitRouterDiagnostic(script, error, performance.now() - started);
            throw error;
          }
          for (const [key, value] of [
            ['tests', count],
            ['pass', count],
            ['fail', 0],
            ['cancelled', 0],
            ['skipped', 0],
            ['todo', 0],
          ] as const) {
            try {
              expect(output).toMatch(new RegExp('^# ' + key + ' ' + value + '$', 'm'));
            } catch (error) {
              emitRouterCountDiagnostic(script, key, performance.now() - started);
              throw error;
            }
          }
        }
      }),
    25_000,
  );
}
