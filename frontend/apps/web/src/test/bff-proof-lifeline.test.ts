// @vitest-environment node
import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { expect, it } from 'vitest';

import { createFixtureOwner, fixtureEnvironment } from '../../scripts/fixture-support.mjs';

const preload = new URL('../../scripts/bff-proof-lifeline.mjs', import.meta.url).href;
const host = new URL('../../scripts/bff-proof-host.mjs', import.meta.url).href;
const fixture = new URL('../../scripts/fixture-support.mjs', import.meta.url).href;

async function bounded<T>(work: Promise<T>, milliseconds = 5000): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([
      work,
      new Promise<never>((_, reject) => {
        timer = setTimeout(
          () => reject(new Error('Owned lifeline control deadline')),
          milliseconds,
        );
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}

async function until(predicate: () => boolean, milliseconds = 5000): Promise<void> {
  const deadline = Date.now() + milliseconds;
  while (!predicate()) {
    if (Date.now() >= deadline) throw new Error('Owned lifeline observation deadline');
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
}

async function ownedControl(args: string[]) {
  const owner = createFixtureOwner();
  let failed = false;
  let failure: unknown;
  let cleanupFailed = false;
  let cleanupFailure: unknown;
  let outcome:
    | { code: number | null; signal: NodeJS.Signals | null; stdout: string; stderr: string }
    | undefined;
  try {
    const { ownBffProofChild } = (await import(host)) as {
      ownBffProofChild(options: {
        owner: ReturnType<typeof createFixtureOwner>;
        app: string;
        args: string[];
        env: NodeJS.ProcessEnv;
        privateValues: string[];
        cookieCanaries: string[];
        scanners: unknown[];
      }): {
        process: ChildProcessWithoutNullStreams;
        completed: Promise<{ code: number | null; signal: NodeJS.Signals | null }>;
      };
    };
    const root = owner.ownRoot(mkdtempSync(join(tmpdir(), 'learnstack-bff-lifeline-control-')));
    const child = ownBffProofChild({
      owner,
      app: root,
      args,
      env: fixtureEnvironment({ root }),
      privateValues: [],
      cookieCanaries: [],
      scanners: [],
    });
    let stdout = '';
    let stderr = '';
    child.process.stdout.on('data', (value: Buffer) => (stdout += value.toString()));
    child.process.stderr.on('data', (value: Buffer) => (stderr += value.toString()));
    const result = await bounded(child.completed);
    outcome = { ...result, stdout, stderr };
  } catch (error) {
    failed = true;
    failure = error;
  } finally {
    try {
      await owner.dispose();
    } catch (error) {
      cleanupFailed = true;
      cleanupFailure = error;
    }
  }
  if (failed && cleanupFailed)
    throw new AggregateError([failure, cleanupFailure], 'Lifeline control and cleanup failed');
  if (failed) throw failure;
  if (cleanupFailed) throw cleanupFailure;
  if (!outcome) throw new Error('Lifeline control produced no outcome');
  return outcome;
}

it('refuses an invalid owner before entering the target', async () => {
  expect(process.platform).not.toBe('win32');
  const owner = createFixtureOwner();
  const root = owner.ownRoot(mkdtempSync(join(tmpdir(), 'learnstack-bff-lifeline-invalid-')));
  try {
    const child = owner.ownChild(
      spawn(process.execPath, ['--import', preload, '-e', "process.stdout.write('entered')"], {
        cwd: root,
        detached: true,
        env: { ...fixtureEnvironment({ root }), LEARNSTACK_TEST_LIFELINE_PARENT: 'invalid' },
        stdio: ['ignore', 'pipe', 'pipe'],
      }),
    );
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (value: Buffer) => (stdout += value.toString()));
    child.stderr.on('data', (value: Buffer) => (stderr += value.toString()));
    const result = await bounded(
      new Promise<{ code: number | null; signal: NodeJS.Signals | null }>((resolve, reject) => {
        child.once('error', reject);
        child.once('close', (code, signal) => resolve({ code, signal }));
      }),
    );
    expect(result).toEqual({ code: 1, signal: null });
    expect(stdout).toBe('');
    expect(stderr).toContain('Invalid BFF lifeline ownership');
  } finally {
    await owner.dispose();
  }
}, 15_000);

it('allows natural completion and keeps fork and inherited worker authority confined', async () => {
  expect(process.platform).not.toBe('win32');
  const root = mkdtempSync(join(tmpdir(), 'learnstack-bff-lifeline-inheritance-'));
  const cleanup = createFixtureOwner();
  cleanup.ownRoot(root);
  try {
    const descendant = join(root, 'descendant.cjs');
    writeFileSync(
      descendant,
      `const { isMainThread, parentPort, workerData } = require('node:worker_threads');
    const value = { envAbsent: process.env.LEARNSTACK_TEST_LIFELINE_PARENT === undefined,
      importPresent: process.execArgv.some((value, index, args) => value === '--import' && args[index + 1] === ${JSON.stringify(preload)}) };
    if (isMainThread) process.send(value);
    else import(workerData.moduleUrl).then(m => parentPort.postMessage({ ...value, inert: m.proofParentWatcher === undefined }));`,
    );
    const program = `
    const { fork } = require('node:child_process');
    const { Worker } = require('node:worker_threads');
    const f = fork(${JSON.stringify(descendant)}, [], { stdio: ['ignore','ignore','ignore','ipc'] });
    f.on('message', forkResult => {
      const w = new Worker(${JSON.stringify(descendant)}, { workerData: { moduleUrl: ${JSON.stringify(preload)} } });
      w.on('message', threadResult => process.stdout.write(JSON.stringify({
        rootEnvAbsent: process.env.LEARNSTACK_TEST_LIFELINE_PARENT === undefined,
        rootImportAbsent: !process.execArgv.some((value, index, args) => value === '--import' && args[index + 1] === ${JSON.stringify(preload)}),
        forkResult, threadResult
      })));
    });`;
    const natural = await ownedControl(['-e', "process.stdout.write('natural')"]);
    expect(natural).toEqual({ code: 0, signal: null, stdout: 'natural', stderr: '' });
    const result = await ownedControl(['-e', program]);
    expect(result.code).toBe(0);
    expect(result.signal).toBeNull();
    expect(result.stderr).toBe('');
    expect(JSON.parse(result.stdout)).toMatchObject({
      rootEnvAbsent: true,
      rootImportAbsent: true,
      forkResult: { envAbsent: true, importPresent: false },
      threadResult: { envAbsent: true, inert: true },
    });
  } finally {
    await cleanup.dispose();
  }
}, 15_000);

it('fails the actual helper child when its ready watcher terminates', async () => {
  expect(process.platform).not.toBe('win32');
  const result = await ownedControl([
    '-e',
    `import(${JSON.stringify(preload)}).then(m => m.proofParentWatcher.terminate())`,
  ]);
  expect(result).toEqual({
    code: 1,
    signal: null,
    stdout: '',
    stderr: 'BFF parent watcher failed\n',
  });
}, 15_000);

it('stops a blocked child group on owner SIGKILL without touching a separate owned group', async () => {
  expect(process.platform).not.toBe('win32');
  const owner = createFixtureOwner();
  // Removal is deferred until both the rescued group and the owner are confirmed stopped.
  const root = mkdtempSync(join(tmpdir(), 'learnstack-bff-lifeline-kill-'));
  const marker = join(root, 'grandchild.json');
  let childPid: number | undefined;
  let childGroupRetired = false;
  let parentStarted = false;
  let testFailed = false;
  let testError: unknown;
  const cleanupErrors: unknown[] = [];
  const groupAlive = () => {
    if (childPid === undefined || childGroupRetired) return false;
    try {
      process.kill(-childPid, 0);
      return true;
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ESRCH') throw error;
      childGroupRetired = true;
      return false;
    }
  };
  try {
    const sentinel = owner.ownChild(
      spawn(process.execPath, ['-e', "process.send('ready');setInterval(()=>{},1000)"], {
        cwd: root,
        env: fixtureEnvironment({ root }),
        detached: true,
        stdio: ['ignore', 'ignore', 'ignore', 'ipc'],
      }),
    );
    const sentinelReady = new Promise<void>((resolve, reject) => {
      sentinel.once('message', () => resolve());
      sentinel.once('error', reject);
    });
    const childCode = `
      require('node:child_process').execFileSync(process.execPath,
        ['-e', "require('node:fs').writeFileSync(process.argv[1], JSON.stringify({pid:process.pid}));setInterval(()=>{},1000)", ${JSON.stringify(marker)}],
        { stdio: 'ignore' });`;
    const program = `
      import { ownBffProofChild } from ${JSON.stringify(host)};
      import { createFixtureOwner, fixtureEnvironment } from ${JSON.stringify(fixture)};
      const owner = createFixtureOwner(); owner.install();
      const child = ownBffProofChild({ owner, app: ${JSON.stringify(root)},
        args: ['-e', ${JSON.stringify(childCode)}], env: fixtureEnvironment({ root: ${JSON.stringify(root)} }),
        privateValues: [], cookieCanaries: [], scanners: [] });
      process.send({ kind: 'child', pid: child.process.pid });
      setInterval(() => {}, 1000);`;
    const parent = owner.ownChild(
      spawn(process.execPath, ['--input-type=module', '-e', program], {
        cwd: root,
        env: fixtureEnvironment({ root }),
        detached: true,
        stdio: ['ignore', 'ignore', 'pipe', 'ipc'],
      }),
    );
    parentStarted = true;
    let parentExited = false;
    parent.on('message', (message: unknown) => {
      if (
        message &&
        typeof message === 'object' &&
        'kind' in message &&
        message.kind === 'child' &&
        'pid' in message &&
        typeof message.pid === 'number'
      )
        childPid = message.pid;
    });
    parent.once('exit', () => (parentExited = true));
    await bounded(sentinelReady);
    await until(() => childPid !== undefined && existsSync(marker));
    const grandchildPid = JSON.parse(readFileSync(marker, 'utf8')).pid as number;
    expect(Number.isSafeInteger(childPid)).toBe(true);
    expect(Number.isSafeInteger(grandchildPid)).toBe(true);
    expect(childPid).not.toBe(sentinel.pid);
    process.kill(grandchildPid, 0);
    process.kill(-parent.pid!, 'SIGKILL');
    await until(() => parentExited);
    await until(() => !groupAlive());
    expect(sentinel.exitCode).toBeNull();
    expect(sentinel.signalCode).toBeNull();
    process.kill(-sentinel.pid!, 0);
  } catch (error) {
    testFailed = true;
    testError = error;
  } finally {
    try {
      // Only this invocation's live, not-yet-retired group is eligible for rescue.
      if (groupAlive()) {
        let signalFailed = false;
        try {
          process.kill(-childPid!, 'SIGKILL');
        } catch (error) {
          if ((error as NodeJS.ErrnoException).code === 'ESRCH') childGroupRetired = true;
          else {
            signalFailed = true;
            cleanupErrors.push(error);
          }
        }
        if (!signalFailed) await until(() => !groupAlive());
      }
    } catch (error) {
      cleanupErrors.push(error);
    } finally {
      // Attempt every owned resource even if the separate group could not be retired.
      try {
        await owner.dispose();
      } catch (error) {
        cleanupErrors.push(error);
      }
    }
    // An unreported child group or failed cleanup leaves ownership unconfirmed.
    if (!cleanupErrors.length && (!parentStarted || childPid !== undefined)) {
      try {
        rmSync(root, { recursive: true, force: true });
      } catch (error) {
        cleanupErrors.push(error);
      }
    }
  }
  if (testFailed && cleanupErrors.length)
    throw new AggregateError([testError, ...cleanupErrors], 'Lifeline control and cleanup failed');
  if (testFailed) throw testError;
  if (cleanupErrors.length) throw new AggregateError(cleanupErrors, 'Lifeline cleanup failed');
}, 15_000);
