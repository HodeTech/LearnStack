// @vitest-environment node
// Runs the finite Node control without starting Next or a browser.
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, realpathSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import { closedFrameworkProofSync } from './framework-proof-closed-boundary';

describe('qualified installed response bodyless and refusal controls', () => {
  it(
    'joins the patched real encoder and handler on HTTP and HTTPS',
    () =>
      closedFrameworkProofSync(() => {
        const directory = mkdtempSync(join(tmpdir(), 'learnstack-bodyless-wrapper-'));
        try {
          const frontendRoot = realpathSync(
            fileURLToPath(new URL('../../../../', import.meta.url)),
          );
          const appRoot = join(frontendRoot, 'apps/web');
          const appPackage: unknown = JSON.parse(
            readFileSync(join(appRoot, 'package.json'), 'utf8'),
          );
          if (
            typeof appPackage !== 'object' ||
            appPackage === null ||
            !('name' in appPackage) ||
            appPackage.name !== '@learnstack/web'
          )
            throw new Error('Bodyless control requires the qualified web package root');
          const runner = realpathSync(
            join(frontendRoot, 'scripts/framework-response-bodyless.test.mjs'),
          );
          const result = spawnSync(process.execPath, [runner], {
            cwd: directory,
            env: { PATH: process.env.PATH, NODE_ENV: 'production' },
            encoding: 'utf8',
            timeout: 25_000,
            maxBuffer: 1024 * 1024,
          });
          expect(result.error).toBeUndefined();
          expect(result.signal).toBeNull();
          expect(result.status).toBe(0);
          expect(result.stderr).toBe('');
          const lines = result.stdout.trim().split('\n');
          expect(lines).toHaveLength(1);
          const line = lines[0];
          if (typeof line !== 'string') throw new Error('Missing bodyless control result');
          const summary: unknown = JSON.parse(line);
          expect(summary).toEqual({
            kind: 'qualified-response-bodyless',
            transports: 2,
            variants: 2,
            normalCases: 25,
            fenceCases: 6,
            total: 124,
            stockRetained: expect.any(Number),
            patchedJoined: true,
            originalPatchedWireEqual: true,
            installedUnchanged: true,
          });
          if (
            typeof summary !== 'object' ||
            summary === null ||
            !('stockRetained' in summary) ||
            typeof summary.stockRetained !== 'number'
          )
            throw new Error('Missing bodyless stock diagnostic count');
          expect(summary.stockRetained).toBeGreaterThan(0);
        } finally {
          rmSync(directory, { recursive: true, force: true });
        }
      }),
    30_000,
  );
});
