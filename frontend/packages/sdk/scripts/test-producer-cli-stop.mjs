import assert from 'node:assert/strict';
import {
  chmodSync,
  existsSync,
  lstatSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { assertNoRetainedFrameworkOwners } from '../../../apps/web/scripts/fixture-support.mjs';
import { createQualifiedPrivateFrameworkSource } from '../../../apps/web/scripts/framework-private-source.mjs';
import {
  frameworkInputGraph,
  resolveQualifiedPnpm,
} from '../../../scripts/framework-installation.mjs';
import { runFrameworkProcessFamily } from '../../../scripts/framework-process-family.mjs';

const frontendRoot = realpathSync(fileURLToPath(new URL('../../../', import.meta.url)));
const appRoot = join(frontendRoot, 'apps/web');
const canonicalGraph = () => frameworkInputGraph({ appRoot, frontendRoot });
const shellQuote = (value) => `'${value.replaceAll("'", "'\\''")}'`;
const mark = (path) => writeFileSync(path, '1\n', { flag: 'wx', mode: 0o600 });
const pause = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function replaceOnce(source, before, after) {
  assert.equal(source.split(before).length, 2, 'Private instrumentation anchor must be unique');
  return source.replace(before, after);
}

async function waitUntil(predicate, deadline) {
  while (!predicate() && Date.now() < deadline) await pause(10);
  return predicate(); // Time alone never proves process retirement.
}

/** Stop the actual CLI while its output leases are held before the first child family. */
export async function checkProducerCliStop(signal) {
  assert.ok(['SIGTERM', 'SIGINT'].includes(signal));
  const expected = canonicalGraph();
  const manager = resolveQualifiedPnpm({ frontendRoot });
  const root = mkdtempSync(
    join(realpathSync(tmpdir()), 'learnstack-framework-ownership-cli-stop-'),
  );
  const controls = join(root, 'controls');
  const held = join(controls, 'held');
  const trigger = join(controls, 'trigger');
  const release = join(controls, 'release');
  const observed = join(controls, 'abort-observed');
  const exitCode = join(controls, 'exit-code');
  let safeToRemove = false;
  let controller;
  let familyPromise;
  let family;
  let outcome;
  let result;

  try {
    mkdirSync(controls, { mode: 0o700 });
    const bin = join(root, 'bin');
    const cache = join(root, 'cache');
    mkdirSync(bin, { mode: 0o700 });
    mkdirSync(cache, { mode: 0o700 });
    const userConfig = join(root, 'user.npmrc');
    const globalConfig = join(root, 'global.npmrc');
    writeFileSync(userConfig, '', { mode: 0o600, flag: 'wx' });
    writeFileSync(globalConfig, '', { mode: 0o600, flag: 'wx' });
    symlinkSync(realpathSync(process.execPath), join(bin, 'node'));
    const shim = join(bin, 'pnpm');
    writeFileSync(
      shim,
      `#!/bin/sh\nexec ${shellQuote(realpathSync(process.execPath))} ${shellQuote(manager.executable)} "$@"\n`,
      { mode: 0o700, flag: 'wx' },
    );
    chmodSync(shim, 0o700);
    const environment = {
      PATH: `${bin}:/usr/bin:/bin`,
      TMPDIR: root,
      NODE_ENV: 'production',
      CI: 'true',
      NPM_CONFIG_USERCONFIG: userConfig,
      NPM_CONFIG_GLOBALCONFIG: globalConfig,
      NPM_CONFIG_CACHE: cache,
    };
    const copy = createQualifiedPrivateFrameworkSource({ root });
    assert.deepEqual(copy.inventory, expected);
    const cli = join(copy.appRoot, 'scripts/framework-build.mjs');
    for (const path of [copy.guardPath, cli]) {
      const entry = lstatSync(path);
      assert.ok(entry.isFile() && !entry.isSymbolicLink());
    }

    const guard = readFileSync(copy.guardPath, 'utf8');
    const anchor = '    cleanFrameworkOutputs(identity, { nativeOnly, owner: lease.record });';
    const barrier =
      `${anchor}\n` +
      `    writeFileSync(${JSON.stringify(held)}, '1\\n', { mode: 0o600, flag: 'wx' });\n` +
      `    while (!existsSync(${JSON.stringify(trigger)})) await new Promise((resolve) => setTimeout(resolve, 10));\n` +
      `    process.kill(process.pid, ${JSON.stringify(signal)});\n` +
      `    while (!existsSync(${JSON.stringify(release)})) await new Promise((resolve) => setTimeout(resolve, 10));`;
    writeFileSync(copy.guardPath, replaceOnce(guard, anchor, barrier));

    const cliSource = readFileSync(cli, 'utf8');
    const cliAnchor =
      'if (process.argv[1] && realpathSync(process.argv[1]) === fileURLToPath(import.meta.url)) {';
    // The copied CLI keeps its real signal handling; these hooks only observe it.
    const observation =
      `import { writeFileSync as probeWrite, existsSync as probeExists } from 'node:fs';\n` +
      `process.on('exit', (code) => probeWrite(${JSON.stringify(exitCode)}, String(code), { mode: 0o600, flag: 'wx' }));\n` +
      `const probeAbort = AbortController.prototype.abort;\n` +
      `AbortController.prototype.abort = function (...args) { const value = probeAbort.apply(this, args); if (probeExists(${JSON.stringify(held)}) && !probeExists(${JSON.stringify(observed)})) probeWrite(${JSON.stringify(observed)}, '1\\n', { mode: 0o600, flag: 'wx' }); return value; };\n`;
    writeFileSync(cli, replaceOnce(cliSource, cliAnchor, observation + cliAnchor));
    const privateGraph = frameworkInputGraph(copy);
    assert.equal(privateGraph.length, expected.length);
    assert.deepEqual(
      privateGraph
        .filter((entry, index) => JSON.stringify(entry) !== JSON.stringify(expected[index]))
        .map((entry) => entry.path),
      ['app/scripts/framework-build.mjs', 'workspace/scripts/framework-installation.mjs'],
    );

    controller = new AbortController();
    const deadline = Date.now() + 60_000;
    familyPromise = runFrameworkProcessFamily(
      process.execPath,
      [cli, '--app-root', copy.appRoot, '--frontend-root', copy.frontendRoot],
      {
        cwd: copy.frontendRoot,
        environment,
        stdio: 'ignore',
        signal: controller.signal,
        onFamily(value) {
          family = value;
        },
      },
    ).then(
      () => {
        outcome = 'resolved';
      },
      () => {
        outcome = 'rejected';
      },
    );
    assert.ok(
      await waitUntil(() => existsSync(held) || outcome !== undefined, deadline),
      'Output claim deadline',
    );
    assert.ok(
      existsSync(held) && family && outcome === undefined,
      'Live producer must hold outputs',
    );
    const appWriter = join(copy.appRoot, '.framework-state/writer.json');
    const sdkWriter = join(copy.frontendRoot, 'packages/sdk/.framework-state/writer.json');
    const writer = JSON.parse(readFileSync(appWriter, 'utf8'));
    assert.ok(
      writer.pid === family.pid && writer.kind === 'build',
      'Authenticated family owns the writer',
    );
    assert.ok(
      typeof writer.nonce === 'string' && /^[0-9a-f]{32}$/.test(writer.nonce),
      'Producer lease nonce is well formed',
    );
    const reader = join(copy.frontendRoot, '.framework-state/readers', writer.nonce);
    assert.ok(
      JSON.stringify(JSON.parse(readFileSync(sdkWriter, 'utf8'))) === JSON.stringify(writer) &&
        JSON.stringify(JSON.parse(readFileSync(reader, 'utf8'))) === JSON.stringify(writer),
      'The same producer owns all three leases',
    );
    for (const path of [
      join(copy.appRoot, '.next'),
      join(copy.appRoot, '.server'),
      join(copy.frontendRoot, 'packages/sdk/dist'),
    ])
      assert.ok(lstatSync(path).isDirectory(), 'Real leased output was claimed');

    mark(trigger);
    assert.ok(
      await waitUntil(() => existsSync(observed) || outcome !== undefined, deadline),
      'Signal observation deadline',
    );
    assert.ok(existsSync(observed), 'CLI signal handler must abort the producer');
    assert.equal(outcome, undefined, 'Producer remains held until release');
    mark(release);
    assert.ok(
      await waitUntil(() => outcome !== undefined, deadline),
      'Process family retirement deadline',
    );
    await familyPromise;
    assert.equal(outcome, 'rejected', 'Cancelled real producer must fail');
    assert.equal(readFileSync(exitCode, 'utf8'), '1', 'CLI must exit with failure');
    for (const path of [reader, appWriter, sdkWriter])
      assert.equal(existsSync(path), false, 'Producer lease retired');
    assertNoRetainedFrameworkOwners(root);
    assert.deepEqual(frameworkInputGraph(copy), privateGraph);
    assert.deepEqual(canonicalGraph(), expected);
    safeToRemove = true;
    result = {
      signalObserved: true,
      familyRetired: true,
      leasesRetired: true,
      sourcesUnchanged: true,
    };
  } finally {
    // On failure unblock only this fixture, request its captured family to stop,
    // and retain all evidence unless retirement and ownership checks passed.
    if (existsSync(controls) && !existsSync(trigger)) {
      try {
        mark(trigger);
      } catch {
        /* Retain the root on any uncertainty. */
      }
    }
    if (existsSync(controls) && !existsSync(release)) {
      try {
        mark(release);
      } catch {
        /* Retain the root on any uncertainty. */
      }
    }
    if (controller && outcome === undefined) controller.abort();
    if (familyPromise && outcome === undefined)
      await waitUntil(() => outcome !== undefined, Date.now() + 15_000);
    if (safeToRemove) rmSync(root, { recursive: true });
  }
  return { ...result, rootDisposed: !existsSync(root) };
}
