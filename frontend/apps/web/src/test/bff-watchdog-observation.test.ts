// @vitest-environment node
import { execFileSync } from 'node:child_process';

import { describe, expect, it } from 'vitest';

describe('BFF actual shutdown watchdog observation', () => {
  it.each([
    ['--with-held-native-stop', 'native-watchdog-observed'],
    ['--without-watchdog-observation', 'native-watchdog-observation'],
  ])(
    '%s preserves failed shutdown and requires its actual deadline observation',
    (mode, stage) => {
      const script = new URL('../../scripts/verify-bff-protocol.mjs', import.meta.url);
      let failure: unknown;
      try {
        execFileSync(process.execPath, [script.pathname, mode], {
          encoding: 'utf8',
          timeout: 150_000,
          maxBuffer: 65536,
          stdio: ['ignore', 'pipe', 'pipe'],
        });
      } catch (error) {
        failure = error;
      }
      expect(failure).toMatchObject({
        status: 1,
        signal: null,
        stdout: '',
      });
      const stderr = (failure as { stderr: string }).stderr;
      if (mode === '--without-watchdog-observation') {
        expect(stderr).toBe('BFF native Next protocol failed: ' + stage + '\n');
      } else {
        const lines = stderr.trimEnd().split('\n');
        expect(lines).toHaveLength(2);
        expect(lines[1]).toBe('BFF native Next protocol failed: ' + stage);
        expect(lines[0]).toMatch(/^BFF watchdog observation: /);
        const observation = JSON.parse(lines[0]!.slice('BFF watchdog observation: '.length));
        expect(Object.keys(observation).sort()).toEqual([
          'beforeClose',
          'beforeRetirement',
          'elapsedMilliseconds',
          'ownerCode',
          'ownerSignal',
          'retired',
          'sample',
        ]);
        expect(observation).toMatchObject({
          beforeClose: true,
          beforeRetirement: true,
          retired: true,
          ownerCode: 1,
          ownerSignal: 'none',
        });
        expect(observation.elapsedMilliseconds).toBeGreaterThanOrEqual(4500);
        expect(observation.elapsedMilliseconds).toBeLessThanOrEqual(120000);
        expect(observation.sample).toHaveLength(5);
        expect(
          observation.sample.every(
            (value: number) => Number.isInteger(value) && value >= 0 && value <= 256,
          ),
        ).toBe(true);
        expect(observation.sample[3]).toBeGreaterThan(0);
        expect(observation.sample[4]).toBeGreaterThan(0);
      }
    },
    160_000,
  );
});
