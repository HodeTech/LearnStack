// @vitest-environment node
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { expect, test } from 'vitest';

import { closedFrameworkProofSync } from './framework-proof-closed-boundary';

test('copied overlap ownership and observation controls pass every causal case', () =>
  closedFrameworkProofSync(() => {
    const frontend = fileURLToPath(new URL('../../../../', import.meta.url));
    const output = execFileSync(
      process.execPath,
      [
        '--test',
        join(frontend, 'scripts/framework-overlap-control.test.mjs'),
        join(frontend, 'scripts/framework-overlap-host.test.mjs'),
        join(frontend, 'scripts/framework-overlap-source.test.mjs'),
      ],
      {
        encoding: 'utf8',
        stdio: ['ignore', 'pipe', 'pipe'],
        timeout: 10_000,
        maxBuffer: 65536,
        env: { PATH: process.env.PATH, NODE_ENV: 'production', NEXT_TELEMETRY_DISABLED: '1' },
      },
    );
    for (const [field, count] of [
      ['tests', 10],
      ['pass', 10],
      ['fail', 0],
      ['cancelled', 0],
      ['skipped', 0],
      ['todo', 0],
    ] as const)
      expect(output).toMatch(new RegExp(`^# ${field} ${count}$`, 'm'));
  }));

test(
  'actual copied UI joins independent owners after the allocated compressor closes',
  () =>
    closedFrameworkProofSync(() => {
      const app = fileURLToPath(new URL('../../', import.meta.url));
      const output = execFileSync(
        process.execPath,
        [join(app, 'scripts/verify-framework-overlap.mjs')],
        {
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
        },
      );
      type Snapshot = {
        aHandler: string;
        bHandler: string;
        aFrame: boolean;
        bFrame: boolean;
        aLease: boolean;
        bLease: boolean;
        bPublished: boolean;
        api: string;
        framework: string;
        render: string;
        snapshot: string;
        stop: string;
        responseClosed: boolean;
        encoderClosed: boolean;
        reserved: number;
        releaseIndex: number;
        diagnosticFault: boolean;
        stageAcknowledged: boolean;
        bTracked: number;
        bBounded: boolean;
      };
      const receipt = JSON.parse(output) as {
        result: { nativeStop: string; reservations: string; bPublished: boolean };
        events: Array<{ event: string; state: Snapshot | null }>;
        transport: Array<{ kind: string; value: string | null }>;
        familyRetired: boolean;
        privateCleanup: boolean;
      };
      expect(Object.keys(receipt).sort()).toEqual([
        'events',
        'familyRetired',
        'privateCleanup',
        'result',
        'transport',
      ]);
      expect(receipt.result).toEqual({
        nativeStop: 'fulfilled',
        reservations: 'settled',
        bPublished: false,
      });
      expect(receipt.familyRetired).toBe(true);
      expect(receipt.privateCleanup).toBe(true);
      expect(receipt.events.length).toBeGreaterThanOrEqual(38);
      expect(receipt.events.length).toBeLessThanOrEqual(128);
      const allowed = new Set([
        'armed',
        'reserve-b',
        'reserve-a',
        'track-b',
        'track-a',
        'bounded-b-tracked',
        'bounded-b-fulfilled',
        'bounded-b-rejected',
        'track-b-fulfilled',
        'track-b-rejected',
        'track-a-fulfilled',
        'track-a-rejected',
        'handler-b-fulfilled',
        'handler-b-rejected',
        'handler-a-fulfilled',
        'handler-a-rejected',
        'frame-b-cleaned',
        'frame-a-cleaned',
        'lease-b-cleaned',
        'lease-a-cleaned',
        'api-entered',
        'api-tracked',
        'api-fulfilled',
        'api-rejected',
        'framework-registered',
        'framework-settled',
        'render-entered',
        'render-settled',
        'snapshot-tracked',
        'snapshot-settled',
        'response-closed',
        'encoder-closed',
        'native-stop-started',
        'native-stop-fulfilled',
        'native-stop-rejected',
        'publish-b',
        'release-render',
        'release-framework',
        'release-snapshot',
        'release-api',
        'snapshot',
      ]);
      const stateKeys = [
        'aHandler',
        'bHandler',
        'aFrame',
        'bFrame',
        'aLease',
        'bLease',
        'bPublished',
        'api',
        'framework',
        'render',
        'snapshot',
        'stop',
        'responseClosed',
        'encoderClosed',
        'reserved',
        'releaseIndex',
        'diagnosticFault',
        'stageAcknowledged',
        'bTracked',
        'bBounded',
      ].sort();
      for (const row of receipt.events) {
        expect(Object.keys(row).sort()).toEqual(['event', 'state']);
        expect(allowed.has(row.event)).toBe(true);
        if (row.event === 'snapshot') {
          expect(row.state).not.toBeNull();
          const state = row.state!;
          expect(Object.keys(state).sort()).toEqual(stateKeys);
          expect(['unknown', 'fulfilled', 'rejected']).toContain(state.aHandler);
          expect(['unknown', 'fulfilled', 'rejected']).toContain(state.bHandler);
          for (const value of [
            state.api,
            state.framework,
            state.render,
            state.snapshot,
            state.stop,
          ])
            expect(['unstarted', 'pending', 'fulfilled', 'rejected']).toContain(value);
          for (const value of [
            state.aFrame,
            state.bFrame,
            state.aLease,
            state.bLease,
            state.bPublished,
            state.responseClosed,
            state.encoderClosed,
            state.diagnosticFault,
            state.stageAcknowledged,
            state.bBounded,
          ])
            expect(typeof value).toBe('boolean');
          expect(
            Number.isInteger(state.reserved) && state.reserved >= 0 && state.reserved <= 2,
          ).toBe(true);
          expect(
            Number.isInteger(state.releaseIndex) &&
              state.releaseIndex >= 0 &&
              state.releaseIndex <= 4,
          ).toBe(true);
          expect(
            Number.isInteger(state.bTracked) && state.bTracked >= 0 && state.bTracked <= 128,
          ).toBe(true);
        } else expect(row.state).toBeNull();
      }
      const events = receipt.events.map((row) => row.event);
      expect(events).not.toContain('publish-b');
      expect(events).not.toContain('native-stop-rejected');
      const one = (name: string) => {
        expect(events.filter((event) => event === name)).toHaveLength(1);
        return events.indexOf(name);
      };
      const oneOf = (names: string[]) => {
        const matches = events.flatMap((event, index) => (names.includes(event) ? [index] : []));
        expect(matches).toHaveLength(1);
        return matches[0]!;
      };
      const before = (first: number, second: number) => expect(first).toBeLessThan(second);
      const phases = receipt.events.flatMap((row, index) =>
        row.event === 'snapshot' && row.state?.stageAcknowledged
          ? [{ index, state: row.state }]
          : [],
      );
      expect(phases.map((phase) => phase.state.releaseIndex)).toEqual([0, 1, 2, 3]);
      for (const { state } of phases) {
        expect(state).toMatchObject({
          stop: 'pending',
          reserved: 2,
          responseClosed: true,
          encoderClosed: true,
          bBounded: true,
          bPublished: false,
          bLease: false,
          api: 'pending',
          diagnosticFault: false,
        });
        expect(state.bTracked).toBeGreaterThan(0);
      }
      expect(phases[0]!.state).toMatchObject({
        render: 'pending',
        framework: 'pending',
        snapshot: 'pending',
        aFrame: false,
        aLease: false,
      });
      expect(phases[1]!.state).toMatchObject({
        render: 'fulfilled',
        framework: 'pending',
        snapshot: 'pending',
        aFrame: false,
        aLease: false,
      });
      expect(phases[2]!.state).toMatchObject({
        framework: 'fulfilled',
        snapshot: 'pending',
        aFrame: true,
        aLease: false,
      });
      expect(phases[3]!.state).toMatchObject({ snapshot: 'fulfilled', aFrame: true, aLease: true });
      before(one('reserve-b'), one('reserve-a'));
      before(one('api-entered'), one('api-tracked'));
      before(one('bounded-b-tracked'), one('native-stop-started'));
      before(one('response-closed'), one('native-stop-started'));
      before(one('encoder-closed'), one('native-stop-started'));
      before(one('native-stop-started'), phases[0]!.index);
      before(phases[0]!.index, one('release-render'));
      before(one('release-render'), one('render-settled'));
      before(one('render-settled'), phases[1]!.index);
      before(phases[1]!.index, one('release-framework'));
      before(one('release-framework'), one('framework-settled'));
      before(one('framework-settled'), phases[2]!.index);
      before(one('frame-a-cleaned'), phases[2]!.index);
      before(phases[2]!.index, one('release-snapshot'));
      before(one('release-snapshot'), one('snapshot-settled'));
      before(one('snapshot-settled'), one('lease-a-cleaned'));
      before(one('lease-a-cleaned'), phases[3]!.index);
      before(phases[3]!.index, one('release-api'));
      before(one('release-api'), oneOf(['api-fulfilled', 'api-rejected']));
      const bWork = oneOf(['bounded-b-fulfilled', 'bounded-b-rejected']);
      before(one('release-api'), bWork);
      before(bWork, one('lease-b-cleaned'));
      before(one('frame-b-cleaned'), one('lease-b-cleaned'));
      before(one('lease-b-cleaned'), one('native-stop-fulfilled'));
      oneOf(['handler-a-fulfilled', 'handler-a-rejected']);
      oneOf(['handler-b-fulfilled', 'handler-b-rejected']);
      expect(receipt.transport.length).toBeGreaterThanOrEqual(5);
      expect(receipt.transport.length).toBeLessThanOrEqual(128);
      for (const row of receipt.transport) {
        expect(Object.keys(row).sort()).toEqual(['kind', 'value']);
        expect([
          'router',
          'allocation',
          'response-finish',
          'response-close',
          'encoder-close',
          'handler-ok',
          'handler-failed',
        ]).toContain(row.kind);
        if (row.kind === 'allocation') expect(row.value).toBe('gzip');
        else if (row.kind === 'encoder-close') expect(row.value).toBe('unended');
        else expect(row.value).toBeNull();
      }
      for (const kind of ['router', 'allocation', 'response-close', 'encoder-close'])
        expect(receipt.transport.filter((row) => row.kind === kind)).toHaveLength(1);
      expect(
        receipt.transport.filter((row) => ['handler-ok', 'handler-failed'].includes(row.kind)),
      ).toHaveLength(1);
    }),
  160_000,
);
