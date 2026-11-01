// @vitest-environment node
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { expect, test } from 'vitest';

import { closedFrameworkProofSync } from './framework-proof-closed-boundary';

const frontend = fileURLToPath(new URL('../../../../', import.meta.url));
const app = join(frontend, 'apps/web');

test(
  'copied router matrix port and generated-source controls pass',
  () =>
    closedFrameworkProofSync(() => {
      for (const [script, count] of [
        ['framework-router-matrix-port.test.mjs', 13],
        ['framework-router-matrix-source.test.mjs', 6],
        ['framework-router-generated-pipes.test.mjs', 26],
      ] as const) {
        const output = execFileSync(
          process.execPath,
          ['--test', join(frontend, 'scripts', script)],
          {
            encoding: 'utf8',
            timeout: 10_000,
            maxBuffer: 65_536,
            env: { PATH: process.env.PATH, NODE_ENV: 'production' },
            stdio: ['ignore', 'pipe', 'pipe'],
          },
        );
        for (const [field, expected] of [
          ['tests', count],
          ['pass', count],
          ['fail', 0],
          ['cancelled', 0],
          ['skipped', 0],
          ['todo', 0],
        ] as const)
          expect(output).toMatch(new RegExp(`^# ${field} ${expected}$`, 'm'));
      }
    }),
  25_000,
);

test(
  'one copied App Router observes filtered identity then conditional HTTPS gzip and HTTP deflate before release',
  () =>
    closedFrameworkProofSync(() => {
      const output = execFileSync(
        process.execPath,
        [join(app, 'scripts/verify-framework-router-matrix.mjs')],
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
      const receipt: unknown = JSON.parse(output);
      if (
        typeof receipt !== 'object' ||
        receipt === null ||
        !('cases' in receipt) ||
        !Array.isArray(receipt.cases) ||
        !('familyRetired' in receipt) ||
        !('privateCleanup' in receipt) ||
        !('measurements' in receipt) ||
        !('pipeProof' in receipt)
      )
        throw new Error('Copied router matrix receipt refused');
      expect(Object.keys(receipt).sort()).toEqual([
        'cases',
        'familyRetired',
        'measurements',
        'pipeProof',
        'privateCleanup',
      ]);
      expect(receipt.familyRetired).toBe(true);
      expect(receipt.privateCleanup).toBe(true);
      expect(receipt.cases).toHaveLength(3);
      const proof = receipt.pipeProof;
      if (typeof proof !== 'object' || proof === null) throw new Error('Copied pipe proof refused');
      expect(Object.keys(proof).sort()).toEqual([
        'filteredBaseline',
        'generatedFiles',
        'installedFiles',
        'inventorySha256',
        'manifestSha256',
        'sites',
        'tracedEntries',
      ]);
      const proofFields = proof as Record<string, unknown>;
      expect(proofFields.filteredBaseline).toBe(true);
      expect(proofFields.installedFiles).toBe(27);
      expect(proofFields.tracedEntries).toBe(3);
      for (const key of ['manifestSha256', 'inventorySha256'])
        expect(proofFields[key]).toMatch(/^[a-f0-9]{64}$/);
      for (const key of ['generatedFiles', 'sites']) {
        const value = proofFields[key];
        if (typeof value !== 'number' || !Number.isSafeInteger(value))
          throw new Error('Copied pipe bound refused');
        expect(value).toBeGreaterThan(0);
        expect(value).toBeLessThanOrEqual(key === 'sites' ? 155 : 128);
      }
      const measurements: unknown = receipt.measurements;
      if (typeof measurements !== 'object' || measurements === null)
        throw new Error('Copied router measurements refused');
      const measured = measurements as Record<string, unknown>;
      expect(Object.keys(measurements).sort()).toEqual([
        'fixtureTotalMs',
        'launchRequestToReadyMs',
        'ownerPid',
        'ownerRssKiB',
        'runtimePid',
        'runtimeRssKiB',
      ]);
      for (const name of [
        'fixtureTotalMs',
        'launchRequestToReadyMs',
        'ownerPid',
        'ownerRssKiB',
        'runtimePid',
        'runtimeRssKiB',
      ] as const) {
        const value = measured[name];
        if (typeof value !== 'number' || !Number.isSafeInteger(value))
          throw new Error('Copied router measurement refused');
        expect(value).toBeGreaterThanOrEqual(0);
      }
      if (typeof measured.ownerPid !== 'number' || typeof measured.runtimePid !== 'number')
        throw new Error('Copied router process roles missing');
      expect(measured.ownerPid).toBeGreaterThan(0);
      expect(measured.runtimePid).toBeGreaterThan(0);
      expect(measured.ownerPid).not.toBe(measured.runtimePid);
      if (
        typeof measured.fixtureTotalMs !== 'number' ||
        typeof measured.launchRequestToReadyMs !== 'number'
      )
        throw new Error('Copied router timing missing');
      expect(measured.fixtureTotalMs).toBeGreaterThanOrEqual(measured.launchRequestToReadyMs);
      if (!('ownerRssKiB' in measured) || !('runtimeRssKiB' in measured))
        throw new Error('Copied router RSS missing');
      expect(measured.ownerRssKiB).toBeGreaterThan(0);
      expect(measured.runtimeRssKiB).toBeGreaterThan(0);
      const census: unknown = JSON.parse(
        readFileSync(join(frontend, 'framework/next-response-cleanup/census.json'), 'utf8'),
      );
      if (
        typeof census !== 'object' ||
        census === null ||
        !('files' in census) ||
        !Array.isArray(census.files)
      )
        throw new Error('Copied router matrix census refused');
      const pipes = new Map<string, string>();
      for (const value of census.files) {
        if (
          typeof value === 'object' &&
          value !== null &&
          'kind' in value &&
          value.kind === 'pipe' &&
          'path' in value &&
          typeof value.path === 'string' &&
          'sha256' in value &&
          typeof value.sha256 === 'string'
        )
          pipes.set(value.path, value.sha256);
      }
      expect(pipes.size).toBe(27);
      for (const [index, value] of receipt.cases.entries()) {
        if (typeof value !== 'object' || value === null)
          throw new Error('Copied router case refused');
        expect(Object.keys(value).sort()).toEqual([
          'case',
          'decodedBytes',
          'encoding',
          'pipeBytes',
          'pipeEntryIndex',
          'pipeKind',
          'pipeManifestSha256',
          'pipePath',
          'pipeSha256',
          'precondition',
          'status',
          'transport',
          'wireBytes',
        ]);
        const selected = [
          {
            case: 'healthy-identity',
            transport: 'https',
            encoding: 'identity',
            precondition: 'unseeded-native-header',
          },
          {
            case: 'healthy-gzip',
            transport: 'https',
            encoding: 'gzip',
            precondition: 'native-string-content-type',
          },
          {
            case: 'healthy-deflate',
            transport: 'http',
            encoding: 'deflate',
            precondition: 'native-string-content-type',
          },
        ][index];
        expect(value).toMatchObject({ ...selected, status: 200, decodedBytes: 8192 });
        if (!('wireBytes' in value) || typeof value.wireBytes !== 'number')
          throw new Error('Copied router wire size refused');
        expect(value.wireBytes).toBeGreaterThan(0);
        expect(value.wireBytes).toBeLessThan(2 * 1024 * 1024);
        if (
          !('pipePath' in value) ||
          typeof value.pipePath !== 'string' ||
          !('pipeSha256' in value) ||
          typeof value.pipeSha256 !== 'string'
        )
          throw new Error('Copied router pipe identity refused');
        const pipe = value as Record<string, unknown>;
        expect(pipe.pipeSha256).toMatch(/^[a-f0-9]{64}$/);
        if (
          typeof pipe.pipeBytes !== 'number' ||
          !Number.isSafeInteger(pipe.pipeBytes) ||
          typeof pipe.pipeEntryIndex !== 'number' ||
          !Number.isSafeInteger(pipe.pipeEntryIndex)
        )
          throw new Error('Copied pipe record bounds refused');
        expect(pipe.pipeBytes).toBeGreaterThan(0);
        expect(pipe.pipeBytes).toBeLessThanOrEqual(4 * 1024 * 1024);
        expect(pipe.pipeEntryIndex).toBeGreaterThanOrEqual(0);
        expect(pipe.pipeEntryIndex).toBeLessThan(8);
        if (pipe.pipeKind === 'installed') {
          expect(pipes.get(value.pipePath)).toBe(value.pipeSha256);
          expect(pipe.pipeManifestSha256).toBeNull();
          expect(pipe.pipeEntryIndex).toBe(0);
        } else {
          expect(pipe.pipeKind).toBe('generated');
          expect(value.pipePath).toMatch(
            /^next\/server\/(?:[a-zA-Z0-9_().@-]+\/)*[a-zA-Z0-9_().@-]+\.js$/,
          );
          expect(pipe.pipeManifestSha256).toBe(proofFields.manifestSha256);
        }
      }
      if (process.platform !== 'darwin' && process.platform !== 'linux')
        throw new Error('Copied router measurement platform refused');
      process.stdout.write(
        'Copied router matrix measurement: ' +
          JSON.stringify({
            platform: process.platform,
            launchRequestToReadyMs: measured.launchRequestToReadyMs,
            fixtureTotalMs: measured.fixtureTotalMs,
            ownerRssKiB: measured.ownerRssKiB,
            runtimeRssKiB: measured.runtimeRssKiB,
          }) +
          '\n',
      );
    }),
  160_000,
);
