import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { DemoProcesses } from './demo-process.mjs';
import { publicSeederEnvironment } from './demo-database.mjs';

// A stand-in for a command whose worker outlives its leader when either
// node-reuse control is missing. This does not execute or model MSBuild.
const worker = `
const { existsSync, writeFileSync } = require('node:fs');
const [ready, release] = process.argv.slice(1);
writeFileSync(ready, 'ready');
const poll = setInterval(() => {
  if (existsSync(release)) { clearInterval(poll); clearTimeout(expiry); }
}, 10);
// Self-retire even if the private gate or carrier disappears.
const expiry = setTimeout(() => clearInterval(poll), 2000);
`;
const leader = `
const { spawn } = require('node:child_process');
const { existsSync } = require('node:fs');
const [ready, release, worker] = process.argv.slice(1);
if (process.env.DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER === '1' && process.env.MSBUILDDISABLENODEREUSE === '1') process.exit(0);
spawn(process.execPath, ['-e', worker, ready, release], { stdio: 'ignore' });
const readyDeadline = setTimeout(() => process.exit(1), 4000);
const poll = setInterval(() => { if (existsSync(ready)) { clearInterval(poll); clearTimeout(readyDeadline); process.exit(0); } }, 10);
`;

for (const missing of [null, 'both', 'DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER', 'MSBUILDDISABLENODEREUSE']) {
  test(`owned setup joins a ${missing ?? 'nonpersistent'} synthetic worker`, { skip: process.platform === 'win32', timeout: 10000 }, async () => {
    const root = mkdtempSync(join(tmpdir(), 'learnstack-owned-node-'));
    const ready = join(root, 'ready');
    const release = join(root, 'release');
    const environment = publicSeederEnvironment(undefined, { PATH: process.env.PATH });
    if (missing === 'both') {
      delete environment.DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER;
      delete environment.MSBUILDDISABLENODEREUSE;
    } else if (missing) delete environment[missing];
    const owner = new DemoProcesses(new AbortController().signal);
    let captured;
    const start = owner.start.bind(owner);
    owner.start = (...args) => { captured = start(...args); return captured; };
    let settled = false;
    const running = owner.run(process.execPath, ['-e', leader, ready, release, worker], {
      env: environment, label: 'synthetic setup', timeout: 5000,
    }).finally(() => { settled = true; });
    let safelyRetired = false;
    try {
      assert.ok(captured);
      const exit = await captured.nativeExit;
      assert.deepEqual(exit, { code: 0, signal: null });
      if (missing) {
        assert.equal(existsSync(ready), true);
        // This is a zero-signal observation of the actual captured process group.
        process.kill(-captured.child.pid, 0);
        await new Promise(resolve => setTimeout(resolve, 100));
        assert.equal(settled, false);
      }
      writeFileSync(release, 'release');
      await running;
      await owner.dispose();
      safelyRetired = true;
      assert.equal(settled, true);
      if (!missing) assert.equal(existsSync(ready), false);
    } finally {
      // Only a private gate releases the known fixture worker. No stale group is signalled.
      if (!existsSync(release)) {
        try { writeFileSync(release, 'release'); } catch { /* Independent worker expiry still applies. */ }
      }
      if (!safelyRetired) {
        await running.catch(() => {});
        await owner.dispose().then(() => { safelyRetired = true; }, () => {});
      }
      if (safelyRetired) rmSync(root, { recursive: true });
    }
  });
}

// The release path's parent is absent, so no gate can be delivered.
test('joins actual worker self-retirement when the private gate is unavailable', { skip: process.platform === 'win32', timeout: 10000 }, async () => {
  const root = mkdtempSync(join(tmpdir(), 'learnstack-owned-node-expiry-'));
  const ready = join(root, 'ready');
  const release = join(root, 'unavailable', 'release');
  const environment = publicSeederEnvironment(undefined, { PATH: process.env.PATH });
  delete environment.MSBUILDDISABLENODEREUSE;
  const owner = new DemoProcesses(new AbortController().signal);
  let captured;
  const start = owner.start.bind(owner);
  owner.start = (...args) => { captured = start(...args); return captured; };
  let settled = false;
  const running = owner.run(process.execPath, ['-e', leader, ready, release, worker], {
    env: environment, label: 'synthetic unavailable gate', timeout: 5000,
  }).finally(() => { settled = true; });
  let safelyRetired = false;
  try {
    assert.ok(captured);
    assert.deepEqual(await captured.nativeExit, { code: 0, signal: null });
    assert.equal(existsSync(ready), true);
    process.kill(-captured.child.pid, 0);
    await new Promise(resolve => setTimeout(resolve, 100));
    assert.equal(settled, false);
    assert.equal(existsSync(release), false);
    // Completion comes from the owner's actual process-group absence check.
    await running;
    await owner.dispose();
    safelyRetired = true;
    assert.equal(settled, true);
    assert.equal(existsSync(release), false);
  } finally {
    if (!safelyRetired) {
      await running.catch(() => {});
      await owner.dispose().then(() => { safelyRetired = true; }, () => {});
    }
    if (safelyRetired) rmSync(root, { recursive: true });
  }
});
