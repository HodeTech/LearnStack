// @vitest-environment node
import { spawn, spawnSync } from 'node:child_process';
import { once } from 'node:events';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import { DemoCommandError, DemoProcesses } from '../../../../../scripts/demo-process.mjs';
import {
  PublicDemoFailure,
  publicDemoChildExited,
  publicDemoFailure,
  publicDemoOperation,
  publicDemoWorkerOutput,
  publicDemoWorkerStage,
} from '../../scripts/public-demo-diagnostics.mjs';
import { stopTestChild } from '../../scripts/stop-test-child.mjs';

const canary = 'private-provider-token-host-url-canary';
const context = { mode: 'audit', laneIndex: 0, kind: 'catalog', run: 2, workerStage: 'audit' };

describe('closed public demo operation diagnostics', () => {
  it.each(['normal', 'signal'])(
    'reports the actual worker %s failure through process ownership',
    async (kind) => {
      const root = mkdtempSync(join(tmpdir(), 'learnstack-demo-worker-status-'));
      const processes = new DemoProcesses(new AbortController().signal);
      let connected: () => void = () => {};
      const connection = new Promise<void>((resolve) => {
        connected = resolve;
      });
      const server = createServer(() => connected());
      await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
      const address = server.address();
      if (!address || typeof address === 'string') throw new Error('Missing control listener');
      const start = processes.start.bind(processes);
      let worker: ReturnType<typeof start> | undefined;
      processes.start = (...args) => {
        worker = start(...args);
        return worker;
      };
      let recorded: unknown;
      const workerInput = { ...context, workerStage: null as string | null };
      try {
        const input = join(root, 'input.json');
        writeFileSync(
          input,
          JSON.stringify({
            ...context,
            port: kind === 'signal' ? address.port : 0,
            result: join(root, 'result.json'),
          }),
        );
        const operation = publicDemoOperation({
          input: workerInput,
          // Deliberately conflicting browser status must never be attributed to the worker.
          child: () => ({ exitCode: 73, signalCode: 'SIGABRT' }),
          run: async (stage) => {
            stage('worker');
            try {
              return await processes.run(
                process.execPath,
                [
                  fileURLToPath(new URL('../../scripts/public-demo-worker.mjs', import.meta.url)),
                  input,
                ],
                { label: 'Worker control', timeout: 15000 },
              );
            } catch (error) {
              try {
                workerInput.workerStage = publicDemoWorkerStage(
                  JSON.parse(readFileSync(join(root, 'result.json'), 'utf8'))?.failure?.stage,
                );
              } catch {
                // Signal death may leave no private worker result.
              }
              throw error;
            }
          },
          cleanup: async () => {},
          record: (row) => {
            recorded ??= row;
          },
        }).catch((error: unknown) => error);
        if (kind === 'signal') {
          await Promise.race([
            connection,
            operation.then(() => {
              throw new Error('Worker exited before control connection');
            }),
          ]);
          if (!worker) throw new Error('Missing owned worker');
          worker.child.kill('SIGTERM');
        }
        const error = await operation;
        expect(error).toBeInstanceOf(DemoCommandError);
        if (!worker) throw new Error('Missing owned worker');
        expect(error).toMatchObject({
          exitCode: worker.child.exitCode,
          signalCode: worker.child.signalCode,
        });
        if (kind === 'normal') expect(worker.child.exitCode).toBe(1);
        else if (process.platform !== 'win32') expect(worker.child.signalCode).toBe('SIGTERM');
        expect(recorded).toMatchObject({
          stage: 'worker',
          reason: 'worker-failure',
          process: 'worker',
          exitCode: worker.child.exitCode,
          signal: worker.child.signalCode,
          workerStage: kind === 'normal' ? 'connect' : null,
        });
        expect(JSON.stringify(recorded)).not.toContain(canary);
      } finally {
        await processes.dispose();
        server.closeAllConnections();
        await new Promise<void>((resolve) => server.close(() => resolve()));
        rmSync(root, { recursive: true, force: true });
      }
    },
    20000,
  );

  it.each([
    'deadline',
    'spawn',
    'cancelled',
    'daemon-before',
    'daemon-during',
    'daemon-spawn',
  ] as const)(
    'keeps actual %s ownership distinct from the stopped worker and browser',
    async (kind) => {
      const controller = new AbortController();
      const processes = new DemoProcesses(controller.signal);
      let worker: ReturnType<DemoProcesses['start']> | undefined;
      let daemon: ReturnType<DemoProcesses['daemon']> | undefined;
      let ready: () => void = () => {};
      const workerReady = new Promise<void>((resolve) => {
        ready = resolve;
      });
      let recorded: ReturnType<typeof publicDemoFailure> | undefined;
      const start = processes.start.bind(processes);
      processes.start = (...args) => {
        worker = start(...args);
        return worker;
      };
      try {
        if (kind.startsWith('daemon')) {
          daemon = processes.daemon(
            kind === 'daemon-spawn'
              ? join(tmpdir(), `missing-demo-daemon-${process.pid}-${Date.now()}`)
              : process.execPath,
            [
              '-e',
              kind === 'daemon-before'
                ? 'process.exit(17)'
                : 'process.on("SIGTERM",()=>process.exit(17));console.log("ready");setInterval(()=>{},1000)',
            ],
            { label: 'Owned daemon control' },
          );
          if (kind === 'daemon-before' || kind === 'daemon-spawn') await processes.failure;
          else if (daemon.child.stdout) await once(daemon.child.stdout, 'data');
          worker = undefined;
        }
        const result = publicDemoOperation({
          input: context,
          child: () => ({ exitCode: 73, signalCode: 'SIGABRT' }),
          record: (row) => {
            recorded ??= row;
          },
          run: async (stage) => {
            stage('worker');
            return processes.run(
              kind === 'spawn'
                ? join(tmpdir(), `missing-demo-command-${process.pid}-${Date.now()}`)
                : process.execPath,
              ['-e', 'console.log("ready");setInterval(()=>{},1000)'],
              {
                label: 'Owned worker control',
                timeout: kind === 'deadline' ? 50 : 15000,
                observe: () => ready(),
              },
            );
          },
          cleanup: async () => {},
        }).catch((error: unknown) => error);
        if (kind === 'cancelled' || kind === 'daemon-during') {
          await Promise.race([
            workerReady,
            result.then(() => {
              throw new Error('Worker ended before the ownership control');
            }),
          ]);
          if (kind === 'cancelled') controller.abort(new Error(canary));
          else daemon!.child.kill('SIGTERM');
        }
        const error = await result;
        expect(error).toBeInstanceOf(DemoCommandError);
        expect(recorded).toMatchObject({
          stage: 'worker',
          reason: kind.startsWith('daemon') ? 'daemon' : kind,
          process: kind.startsWith('daemon') ? 'daemon' : 'worker',
          exitCode:
            kind.startsWith('daemon') && kind !== 'daemon-spawn' ? daemon!.child.exitCode : null,
          signal:
            kind.startsWith('daemon') && kind !== 'daemon-spawn' ? daemon!.child.signalCode : null,
        });
        expect(JSON.stringify(recorded)).not.toContain(canary);
        if (kind === 'daemon-before' || kind === 'daemon-spawn') expect(worker).toBeUndefined();
        else if (worker) expect(publicDemoChildExited(worker.child)).toBe(true);
      } finally {
        await processes.dispose();
      }
    },
    20000,
  );

  it('a real child stopped before profile-removal refusal supplies no browser failure status', async () => {
    const processes = new DemoProcesses(new AbortController().signal);
    const browser = processes.start(
      process.execPath,
      ['-e', 'console.log("ready");setInterval(()=>{},1000)'],
      { label: 'Owned browser control' },
    );
    let recorded: ReturnType<typeof publicDemoFailure> | undefined;
    const error = new PublicDemoFailure('profile-cleanup', new Error(canary));
    try {
      if (browser.child.stdout) await once(browser.child.stdout, 'data');
      await expect(
        publicDemoOperation({
          input: context,
          child: () => browser.child,
          record: (row) => {
            recorded ??= row;
          },
          run: async (stage) => {
            stage('result');
            return 'complete';
          },
          cleanup: async () => {
            await stopTestChild(browser.child);
            throw error;
          },
        }),
      ).rejects.toBe(error);
      expect(publicDemoChildExited(browser.child)).toBe(true);
      expect(recorded).toMatchObject({
        stage: 'cleanup',
        reason: 'profile-cleanup',
        process: null,
        exitCode: null,
        signal: null,
      });
      expect(JSON.stringify(recorded)).not.toContain(canary);
    } finally {
      await processes.dispose();
    }
  });

  it('emits only complete closed worker lines when dependency output shares a chunk', () => {
    expect(
      publicDemoWorkerOutput(Buffer.from(`Public demo worker refused: audit\n${canary}\n`)),
    ).toEqual(['Public demo worker refused: audit']);
    expect(
      publicDemoWorkerOutput(Buffer.from(`Public demo worker refused: audit ${canary}\n`)),
    ).toEqual([]);
    expect(publicDemoWorkerOutput(Buffer.from(`Public demo worker refused: ${canary}\n`))).toEqual(
      [],
    );
  });

  it.each(['missing', 'malformed'])(
    'the actual worker attributes %s input to input rather than a CDP connection',
    (kind) => {
      const root = mkdtempSync(join(tmpdir(), 'learnstack-demo-input-status-'));
      try {
        const input = join(root, 'input.json');
        writeFileSync(input, '{"private":"' + canary);
        const execution = spawnSync(
          process.execPath,
          [
            fileURLToPath(new URL('../../scripts/public-demo-worker.mjs', import.meta.url)),
            ...(kind === 'malformed' ? [input] : []),
          ],
          { encoding: 'utf8', timeout: 15000 },
        );
        expect(execution.status).toBe(1);
        expect(execution.stderr).toContain('Public demo worker refused: input');
        expect(execution.stdout + execution.stderr).not.toContain(canary);
      } finally {
        rmSync(root, { recursive: true, force: true });
      }
    },
  );
  it.each(['normal', 'signal'])(
    'recognizes an actual native child %s exit before the startup deadline',
    async (kind) => {
      const child = spawn(
        process.execPath,
        ['-e', kind === 'normal' ? 'process.exit(0)' : 'setInterval(() => {}, 1000)'],
        { stdio: 'ignore' },
      );
      const operationChild = (() => child) satisfies Parameters<
        typeof publicDemoOperation
      >[0]['child'];
      const exited = once(child, 'exit');
      try {
        expect(publicDemoChildExited(child)).toBe(false);
        if (kind === 'signal') child.kill('SIGTERM');
        await exited;
        expect(publicDemoChildExited(child)).toBe(true);
        const status = { exitCode: child.exitCode, signalCode: child.signalCode };
        if (kind === 'normal') expect(status).toEqual({ exitCode: 0, signalCode: null });
        else if (process.platform !== 'win32')
          expect(status).toEqual({ exitCode: null, signalCode: 'SIGTERM' });
        else expect(status.exitCode !== null || status.signalCode !== null).toBe(true);
        expect(
          publicDemoFailure(
            'startup-port',
            new PublicDemoFailure('chromium-exited'),
            context,
            operationChild(),
          ),
        ).toMatchObject({
          reason: 'chromium-exited',
          exitCode: status.exitCode,
          signal: status.signalCode,
        });
      } finally {
        if (!publicDemoChildExited(child)) child.kill('SIGKILL');
        await exited;
      }
    },
  );

  it('returns a successful operation result only after owned cleanup without recording failure', async () => {
    const events: string[] = [];
    const result = await publicDemoOperation({
      input: context,
      child: () => undefined,
      record: () => {
        events.push('failure');
      },
      run: async (phase) => {
        phase('result');
        events.push('result');
        return { complete: true };
      },
      cleanup: async () => {
        events.push('cleanup');
      },
    });
    expect(result).toEqual({ complete: true });
    expect(events).toEqual(['result', 'cleanup']);
  });

  it.each([
    ['browser-spawn', 'unknown'],
    ['startup-port', 'chromium-exited'],
    ['startup-port', 'port-invalid'],
    ['startup-port', 'deadline'],
    ['worker', 'worker-failure'],
    ['result', 'unknown'],
  ])('records a failure at %s with closed reason %s', async (stage, reason) => {
    let failure: unknown;
    let cleanups = 0;
    const error =
      reason === 'unknown' || reason === 'worker-failure'
        ? Object.assign(new Error(canary), { name: canary, code: canary })
        : Object.assign(new PublicDemoFailure(reason), { message: canary });
    const operation = publicDemoOperation({
      input: { ...context, url: canary, token: canary },
      child: () => ({ exitCode: 1, signalCode: null, stderr: canary }),
      record: (row: unknown) => {
        failure ??= row;
      },
      run: async (phase: (value: string) => void) => {
        phase(stage);
        throw error;
      },
      cleanup: async () => {
        cleanups++;
      },
    });
    await expect(operation).rejects.toBe(error);
    expect(failure).toEqual({
      stage,
      reason,
      process: reason === 'chromium-exited' ? 'browser' : null,
      mode: 'audit',
      lane: 1,
      kind: 'catalog',
      run: 2,
      workerStage: stage === 'worker' ? 'audit' : null,
      exitCode: reason === 'chromium-exited' ? 1 : null,
      signal: null,
    });
    expect(JSON.stringify(failure)).not.toContain(canary);
    expect(cleanups).toBe(1);
  });

  it('records cleanup failure without converting a successful operation into a pass', async () => {
    let failure: unknown;
    const error = new Error(canary);
    await expect(
      publicDemoOperation({
        input: context,
        child: () => ({ exitCode: 0, signalCode: null }),
        record: (row: unknown) => {
          failure ??= row;
        },
        run: async (phase: (value: string) => void) => {
          phase('result');
          return 'complete';
        },
        cleanup: async () => {
          throw error;
        },
      }),
    ).rejects.toBe(error);
    expect(failure).toEqual({
      stage: 'cleanup',
      reason: 'cleanup-failure',
      process: null,
      mode: 'audit',
      lane: 1,
      kind: 'catalog',
      run: 2,
      workerStage: null,
      exitCode: null,
      signal: null,
    });
    expect(JSON.stringify(failure)).not.toContain(canary);
  });

  it('retains the first worker failure and phase when cleanup also fails', async () => {
    let failure: unknown;
    const original = new Error(canary);
    await expect(
      publicDemoOperation({
        input: context,
        child: () => ({ exitCode: null, signalCode: null }),
        record: (row: unknown) => {
          failure ??= row;
        },
        run: async (phase: (value: string) => void) => {
          phase('worker');
          throw original;
        },
        cleanup: async () => {
          throw new PublicDemoFailure('deadline');
        },
      }),
    ).rejects.toBe(original);
    expect(failure).toMatchObject({ stage: 'worker', reason: 'worker-failure' });
    expect(JSON.stringify(failure)).not.toContain(canary);
  });

  it('does not export arbitrary error, context, stage or process fields', () => {
    const failure = publicDemoFailure(
      canary,
      Object.assign(new Error(canary), {
        reason: canary,
        name: canary,
        code: canary,
        stack: canary,
      }),
      {
        mode: canary,
        laneIndex: canary,
        kind: canary,
        run: canary,
        url: canary,
        workerStage: canary,
      },
      Object.assign({ exitCode: canary, signalCode: canary }, { stderr: canary }),
    );
    expect(failure).toEqual({
      stage: 'proof',
      reason: 'unknown',
      process: null,
      mode: null,
      lane: null,
      kind: null,
      run: null,
      workerStage: null,
      exitCode: null,
      signal: null,
    });
    expect(JSON.stringify(failure)).not.toContain(canary);
    const poisoned = Object.assign(new PublicDemoFailure('deadline'), { reason: canary });
    const worker = publicDemoFailure('worker', poisoned, { ...context, workerStage: canary });
    expect(worker).toMatchObject({ reason: 'worker-failure', workerStage: null });
    expect(JSON.stringify(worker)).not.toContain(canary);
  });

  it('the actual worker publishes only its closed failed phase before a browser connection', () => {
    const root = mkdtempSync(join(tmpdir(), 'learnstack-demo-diagnostics-'));
    try {
      const input = join(root, 'input.json');
      const result = join(root, 'result.json');
      writeFileSync(
        input,
        JSON.stringify({
          ...context,
          port: 0,
          result,
          url: `https://${canary}.invalid`,
          provider: canary,
        }),
      );
      const execution = spawnSync(
        process.execPath,
        [fileURLToPath(new URL('../../scripts/public-demo-worker.mjs', import.meta.url)), input],
        { encoding: 'utf8', timeout: 15000 },
      );
      expect(execution.status).toBe(1);
      expect(JSON.parse(readFileSync(result, 'utf8'))).toEqual({ failure: { stage: 'connect' } });
      expect(execution.stderr).toContain('Public demo worker refused: connect');
      expect(execution.stdout + execution.stderr + readFileSync(result, 'utf8')).not.toContain(
        canary,
      );
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  });

  it('attributes an actual toolchain refusal to preflight before CDP connection', () => {
    const root = mkdtempSync(join(tmpdir(), 'learnstack-demo-toolchain-status-'));
    try {
      const input = join(root, 'input.json');
      const result = join(root, 'result.json');
      writeFileSync(input, JSON.stringify({ port: 0, result }));
      const execution = spawnSync(
        process.execPath,
        [
          '--input-type=module',
          '-e',
          `
        import fs from 'node:fs';
        import { syncBuiltinESMExports } from 'node:module';
        const read = fs.readFileSync;
        fs.readFileSync = (path, ...args) => String(path).endsWith('/frontend/package.json')
          ? JSON.stringify({ devDependencies: { lighthouse: '0.0.0', playwright: '1.64.0' } })
          : read(path, ...args);
        syncBuiltinESMExports();
        process.argv[2] = ${JSON.stringify(input)};
        await import(${JSON.stringify(new URL('../../scripts/public-demo-worker.mjs', import.meta.url).href)});
      `,
        ],
        { encoding: 'utf8', timeout: 15000 },
      );
      expect(execution.status).toBe(1);
      expect(JSON.parse(readFileSync(result, 'utf8'))).toEqual({ failure: { stage: 'preflight' } });
      expect(execution.stderr).toContain('Public demo worker refused: preflight');
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  });
});
