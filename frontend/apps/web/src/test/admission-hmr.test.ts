// @vitest-environment node
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

import { verifyHmrAdmission } from '../../scripts/admission-hmr.mjs';

describe('development admission proof controls', () => {
  it.each(['clean', 'old-site', 'old-source', 'active-context', 'retained-snapshot', 'bad-status'])(
    '%s observes the intended freshness/lifetime boundary and restores owned source',
    async (mode) => {
      const app = mkdtempSync(join(tmpdir(), 'learnstack-hmr-control-'));
      const page = join(app, 'src/app/(studio)/studio/page.tsx');
      mkdirSync(join(app, 'src/app/(studio)/studio'), { recursive: true });
      writeFileSync(page, 'original disposable studio source');
      const child = { pid: 42, exitCode: null };
      const checkpoints: string[] = [];
      let calls = 0;
      let stopped = false;
      let watcherClosed = false;
      try {
        const run = verifyHmrAdmission({
          app,
          configuration: { tenants: [{ name: 'First institution' }] },
          checkpoint: async (name) => {
            checkpoints.push(name);
          },
          startNative: async (dev) => {
            expect(dev).toBe(true);
            return child;
          },
          stopNative: async (value) => {
            expect(value).toBe(child);
            stopped = true;
          },
          observe: async () => ({
            active: mode === 'active-context' ? 1 : 0,
            snapshots: mode === 'retained-snapshot' ? 1 : 0,
          }),
          watchSource: async () => ({
            afterWrite: async (write) => {
              expect(calls).toBe(1);
              write();
            },
            close: () => {
              watcherClosed = true;
            },
          }),
          call: async (_path, options) => {
            calls++;
            const after = calls === 2;
            expect(readFileSync(page, 'utf8')).toContain(
              after ? 'admission-hmr-after' : 'admission-hmr-before',
            );
            if (after) expect(options?.headers).toEqual({ RSC: '1', 'Next-Hmr-Refresh': '1' });
            return {
              status: mode === 'bad-status' ? 503 : 200,
              headers: { 'content-type': after ? 'text/x-component' : 'text/html' },
              body:
                'First institution' +
                (after && mode !== 'old-site' ? ' HMR refresh' : '') +
                (after && mode !== 'old-source' ? ' admission-hmr-after' : ' admission-hmr-before'),
            };
          },
        });
        if (mode === 'clean') {
          await expect(run).resolves.toBeUndefined();
          expect(checkpoints).toEqual(['hmr-before', 'hmr-change', 'hmr-after', 'hmr-restore']);
          expect(calls).toBe(2);
          expect(watcherClosed).toBe(true);
        } else await expect(run).rejects.toThrow();
        expect(stopped).toBe(true);
        expect(readFileSync(page, 'utf8')).toBe('original disposable studio source');
      } finally {
        rmSync(app, { recursive: true, force: true });
      }
    },
  );
});
