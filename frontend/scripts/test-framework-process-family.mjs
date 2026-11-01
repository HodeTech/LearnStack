// Real process-family controls. Inert compilers write only disposable output.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import {
  copyFileSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';
import {
  acquireFrameworkLease,
  FRAMEWORK_FRONTEND_ROOT,
  resolveQualifiedPnpm,
} from './framework-installation.mjs';
import { runFrameworkProcessFamily } from './framework-process-family.mjs';

const self = fileURLToPath(import.meta.url);
function put(path, value) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, value);
}
async function observed(predicate) {
  const began = performance.now();
  while (!predicate()) {
    if (performance.now() - began > 2500)
      throw new Error('Process-family control observation failed');
    await delay(5);
  }
}
function alive(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    if (error.code === 'ESRCH') return false;
    throw error;
  }
}
async function owner(frontendRoot, appRoot, script, kind, signalAudit, managerCLI) {
  const kill = process.kill.bind(process);
  process.kill = (pid, signal) => {
    if (pid < 0 && signal !== 0) {
      writeFileSync(signalAudit, 'blocked cached numeric process-group signal');
      throw new Error('Control prevented cached process-group signaling');
    }
    return kill(pid, signal);
  };
  const lease = acquireFrameworkLease({ frontendRoot, appRoot, kind });
  try {
    await runFrameworkProcessFamily(
      process.execPath,
      managerCLI ? [managerCLI, 'run', '--silent', 'owned_compile'] : [script],
      {
        cwd: managerCLI ? script : dirname(script),
        environment: {
          PATH: process.env.PATH,
          LEARNSTACK_FRAMEWORK_PRODUCER_OWNER: lease.record.nonce,
        },
        stdio: 'ignore',
      },
    );
  } catch {
    process.exitCode = 1;
  } finally {
    lease.release();
  }
}

export async function runFrameworkFamilyControl(mode) {
  assert.ok(
    [
      'sdk-cancel',
      'sdk-direct-death',
      'next-exit',
      'next-success',
      'late-cancel',
      'installer-exit',
      'pnpm-lifecycle',
      'owner-death',
    ].includes(mode),
  );
  const root = mkdtempSync(join(tmpdir(), 'learnstack-framework-family-'));
  const frontendRoot = join(root, 'frontend');
  const appRoot = join(frontendRoot, 'apps/web');
  const sdk = join(frontendRoot, 'packages/sdk');
  const ready = join(root, 'compiler-ready.json');
  const release = join(root, 'release-compiler');
  const late = join(sdk, 'dist/actual-compiler-write.js');
  const signalAudit = join(root, 'forbidden-parent-signal');
  const selfSignal = join(root, 'actual-group-signal');
  const compiler = join(sdk, 'node_modules/typescript/bin/tsc');
  put(join(sdk, 'package.json'), '{"name":"@learnstack/sdk"}\n');
  put(join(sdk, 'src/generated/schema.d.ts'), 'export {};\n');
  mkdirSync(join(sdk, 'dist'), { recursive: true });
  mkdirSync(join(appRoot, 'node_modules/@learnstack'), { recursive: true });
  symlinkSync(sdk, join(appRoot, 'node_modules/@learnstack/sdk'), 'dir');
  symlinkSync(join(FRAMEWORK_FRONTEND_ROOT, 'scripts'), join(frontendRoot, 'scripts'), 'dir');
  put(
    join(sdk, 'node_modules/typescript/package.json'),
    '{"name":"typescript","version":"0.0.0"}\n',
  );
  put(
    compiler,
    `
const fs = require('node:fs');
process.on('SIGTERM', () => fs.writeFileSync(${JSON.stringify(selfSignal)}, 'actual SIGTERM'));
process.on('SIGINT', () => fs.writeFileSync(${JSON.stringify(selfSignal)}, 'actual SIGINT'));
fs.writeFileSync(${JSON.stringify(ready)}, JSON.stringify({pid:process.pid,parent:process.ppid}));
const timer = setInterval(() => {
  if (!fs.existsSync(${JSON.stringify(release)})) return;
  fs.writeFileSync(${JSON.stringify(late)}, 'actual descendant mutation');
  clearInterval(timer);
}, 5);
`,
  );
  const sdkScript = join(sdk, 'scripts/build-native.mjs');
  mkdirSync(dirname(sdkScript), { recursive: true });
  copyFileSync(join(FRAMEWORK_FRONTEND_ROOT, 'packages/sdk/scripts/build-native.mjs'), sdkScript);
  const wrapper = join(root, 'wrapper.cjs');
  put(
    wrapper,
    `
const { fork } = require('node:child_process');
fork(${JSON.stringify(compiler)}, [], {stdio:'ignore'});
process.on('SIGTERM', () => process.exit(143));
process.on('SIGINT', () => process.exit(130));
${['next-success', 'late-cancel'].includes(mode) ? 'setTimeout(() => process.exit(0), 100);' : 'setInterval(() => {}, 1000);'}
`,
  );
  const kind = ['installer-exit', 'pnpm-lifecycle'].includes(mode) ? 'install' : 'native';
  const manager = mode === 'pnpm-lifecycle' ? resolveQualifiedPnpm() : undefined;
  const managerRoot = join(root, 'manager');
  if (manager) {
    const quote = (value) => `'${value.replaceAll("'", "'\\''")}'`;
    put(
      join(managerRoot, 'package.json'),
      JSON.stringify({
        name: 'owned-lifecycle-control',
        private: true,
        scripts: { owned_compile: `${quote(process.execPath)} ${quote(wrapper)}` },
      }),
    );
  }
  const child = spawn(
    process.execPath,
    [
      self,
      '--owner',
      frontendRoot,
      appRoot,
      manager
        ? managerRoot
        : mode.startsWith('sdk-') || mode === 'owner-death'
          ? sdkScript
          : wrapper,
      kind,
      signalAudit,
      manager?.executable ?? '',
    ],
    {
      stdio: ['ignore', 'pipe', 'pipe'],
      env: { PATH: process.env.PATH },
    },
  );
  let stderr = '';
  child.stderr.on('data', (bytes) => {
    stderr += bytes;
  });
  const closed = new Promise((resolve) =>
    child.once('close', (code, signal) => resolve({ code, signal })),
  );
  let actual;
  try {
    await observed(() => existsSync(ready) || child.exitCode !== null);
    assert.ok(existsSync(ready), stderr);
    actual = JSON.parse(readFileSync(ready));
    if (mode === 'sdk-cancel' || mode === 'pnpm-lifecycle') child.kill('SIGTERM');
    else if (mode === 'owner-death') child.kill('SIGKILL');
    else if (mode === 'next-success') await observed(() => !alive(actual.parent));
    else if (mode === 'late-cancel') {
      await observed(() => !alive(actual.parent));
      child.kill('SIGTERM');
      child.kill('SIGINT');
    } else process.kill(actual.parent, mode === 'next-exit' ? 'SIGTERM' : 'SIGKILL');
    // This is a bounded negative observation, never a retirement witness.
    await delay(35);
    if (mode !== 'owner-death') assert.equal(child.exitCode, null);
    assert.equal(alive(actual.pid), true);
    assert.equal(existsSync(late), false);
    assert.equal(existsSync(signalAudit), false);
    if (mode === 'pnpm-lifecycle') assert.equal(existsSync(selfSignal), true);
    assert.throws(
      () => acquireFrameworkLease({ frontendRoot, appRoot, kind }),
      (error) => error.reason === 'owner_busy',
    );
    const retained =
      kind === 'install'
        ? existsSync(join(frontendRoot, '.framework-state/writer.json'))
        : existsSync(join(appRoot, '.framework-state/writer.json')) &&
          existsSync(join(sdk, '.framework-state/writer.json'));
    assert.equal(retained, true);
    put(release, 'allow actual descendant completion');
    await observed(() => !alive(actual.pid));
    await observed(() => !alive(actual.parent));
    await observed(() => child.exitCode !== null || child.signalCode !== null);
    const status = await closed;
    assert.deepEqual(
      status,
      mode === 'owner-death'
        ? { code: null, signal: 'SIGKILL' }
        : { code: mode === 'next-success' ? 0 : 1, signal: null },
    );
    assert.equal(alive(actual.pid), false);
    assert.equal(existsSync(late), true);
    assert.equal(existsSync(signalAudit), false);
    if (mode === 'owner-death')
      assert.throws(
        () => acquireFrameworkLease({ frontendRoot, appRoot, kind }),
        (error) => error.reason === 'owner_busy',
      );
    else {
      const replacement = acquireFrameworkLease({ frontendRoot, appRoot, kind });
      replacement.release();
    }
    return { mode, result: 'pass', retainedBeforeActualClosure: true, actualDescendantWrite: true };
  } finally {
    put(release, 'cleanup only after actual completion');
    if (actual && alive(actual.pid)) await observed(() => !alive(actual.pid));
    if (child.exitCode === null) {
      child.kill('SIGTERM');
      await closed;
    }
    rmSync(root, { recursive: true, force: true });
  }
}

if (process.argv[2] === '--owner') await owner(...process.argv.slice(3));
