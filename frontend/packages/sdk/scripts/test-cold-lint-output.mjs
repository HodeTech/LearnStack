import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import {
  constants,
  copyFileSync,
  existsSync,
  lstatSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  realpathSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

import { assertNoRetainedFrameworkOwners } from '../../../apps/web/scripts/fixture-support.mjs';
import { createQualifiedPrivateFrameworkSource } from '../../../apps/web/scripts/framework-private-source.mjs';
import {
  acquireFrameworkLease,
  cleanFrameworkOutputs,
  frameworkInputGraph,
  FrameworkAdmissionError,
  resolveQualifiedPnpm,
} from '../../../scripts/framework-installation.mjs';
import { runFrameworkProcessFamily } from '../../../scripts/framework-process-family.mjs';

const frontendRoot = realpathSync(fileURLToPath(new URL('../../../', import.meta.url)));
const repositoryRoot = dirname(frontendRoot);
const appRoot = join(frontendRoot, 'apps/web');
const canonicalGraph = () => frameworkInputGraph({ appRoot, frontendRoot });
const hash = (bytes) => createHash('sha256').update(bytes).digest('hex');
const shellQuote = (value) => `'${value.replaceAll("'", "'\\''")}'`;

function regularBytes(path) {
  const entry = lstatSync(path);
  assert.ok(entry.isFile() && !entry.isSymbolicLink() && entry.size <= 10_000_000);
  assert.equal(realpathSync(path), path);
  const bytes = readFileSync(path);
  assert.equal(bytes.length, entry.size);
  return bytes;
}

function snapshot(root) {
  const entries = [];
  function visit(directory) {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const path = join(directory, entry.name);
      assert.equal(entry.isSymbolicLink(), false);
      if (entry.isDirectory()) visit(path);
      else {
        assert.ok(entry.isFile());
        const bytes = regularBytes(path);
        entries.push({ path: relative(root, path), bytes: bytes.length, sha256: hash(bytes) });
      }
    }
  }
  if (existsSync(root)) visit(root);
  return entries.sort((a, b) => a.path.localeCompare(b.path, 'en'));
}

function copyExtraInputs(copy, root) {
  // Enumerate tracked scripts only, under a test-owned exclusion and closed Git config.
  const excludes = join(root, 'git-excludes');
  writeFileSync(excludes, '.claude/worktrees/\n', { flag: 'wx', mode: 0o600 });
  const exclusion = lstatSync(excludes);
  assert.ok(exclusion.isFile() && !exclusion.isSymbolicLink());
  assert.equal(exclusion.mode & 0o777, 0o600);
  assert.equal(exclusion.size, 19);
  assert.equal(readFileSync(excludes, 'utf8'), '.claude/worktrees/\n');
  const gitEnvironment = {
    PATH: '/usr/bin:/bin',
    GIT_CONFIG_NOSYSTEM: '1',
    GIT_CONFIG_GLOBAL: '/dev/null',
    GIT_CONFIG_COUNT: '1',
    GIT_CONFIG_KEY_0: 'core.excludesFile',
    GIT_CONFIG_VALUE_0: excludes,
  };
  const output = execFileSync(
    'git',
    ['-c', `core.excludesFile=${excludes}`, 'ls-files', '-z', '--', 'scripts'],
    {
      cwd: repositoryRoot,
      env: gitEnvironment,
      maxBuffer: 1_000_000,
      timeout: 10_000,
    },
  );
  const scripts = output.toString('utf8').split('\0').filter(Boolean);
  assert.ok(
    scripts.length > 0 &&
      scripts.every((name) => name.startsWith('scripts/') && !name.split('/').includes('..')),
  );
  const paths = [
    ...scripts,
    'backend/openapi/v1.json',
    'frontend/packages/sdk/.eslintrc.cjs',
    'frontend/packages/ui/.eslintrc.cjs',
  ];
  assert.equal(new Set(paths).size, paths.length);
  const destinationRoot = dirname(copy.frontendRoot);
  return paths.map((name) => {
    const original = join(repositoryRoot, name);
    const target = join(destinationRoot, name);
    const bytes = regularBytes(original);
    mkdirSync(dirname(target), { recursive: true, mode: 0o700 });
    copyFileSync(original, target, constants.COPYFILE_EXCL);
    assert.ok(regularBytes(target).equals(bytes));
    return { original, target, sha256: hash(bytes) };
  });
}

function verifyExtraInputs(inputs) {
  for (const { original, target, sha256 } of inputs) {
    assert.equal(hash(regularBytes(original)), sha256);
    assert.equal(hash(regularBytes(target)), sha256);
  }
}

/** Run the real CI prefix in a qualified cold source, then exercise the real build guard. */
export async function checkColdLintOutput(cached) {
  assert.equal(typeof cached, 'boolean');
  const expected = canonicalGraph();
  const manager = resolveQualifiedPnpm({ frontendRoot });
  const root = mkdtempSync(
    join(realpathSync(tmpdir()), 'learnstack-framework-ownership-cold-lint-'),
  );
  let safeToRemove = false;
  let result;
  try {
    const bin = join(root, 'bin');
    const cache = join(root, 'cache');
    mkdirSync(bin, { mode: 0o700 });
    mkdirSync(cache, { mode: 0o700 });
    const userConfig = join(root, 'user.npmrc');
    const globalConfig = join(root, 'global.npmrc');
    writeFileSync(userConfig, '', { flag: 'wx', mode: 0o600 });
    writeFileSync(globalConfig, '', { flag: 'wx', mode: 0o600 });
    symlinkSync(realpathSync(process.execPath), join(bin, 'node'));
    writeFileSync(
      join(bin, 'pnpm'),
      `#!/bin/sh\nexec ${shellQuote(realpathSync(process.execPath))} ${shellQuote(manager.executable)} "$@"\n`,
      { flag: 'wx', mode: 0o700 },
    );
    const environment = {
      PATH: `${bin}:/usr/bin:/bin`,
      TMPDIR: root,
      CI: 'true',
      NODE_ENV: 'production',
      NPM_CONFIG_USERCONFIG: userConfig,
      NPM_CONFIG_GLOBALCONFIG: globalConfig,
      NPM_CONFIG_CACHE: cache,
      GIT_CONFIG_NOSYSTEM: '1',
      GIT_CONFIG_GLOBAL: '/dev/null',
      GIT_CONFIG_COUNT: '1',
      GIT_CONFIG_KEY_0: 'core.excludesFile',
      GIT_CONFIG_VALUE_0: join(root, 'git-excludes'),
    };
    const copy = createQualifiedPrivateFrameworkSource({ root });
    assert.deepEqual(copy.inventory, expected);
    const extras = copyExtraInputs(copy, root);
    const lint = join(copy.appRoot, 'scripts/lint-web.mjs');
    if (cached) {
      const source = regularBytes(lint).toString('utf8');
      const anchor = "          '--no-cache',\n";
      assert.equal(source.split(anchor).length, 2);
      writeFileSync(lint, source.replace(anchor, ''));
    }
    const before = frameworkInputGraph(copy);
    assert.equal(before.length, expected.length);
    assert.deepEqual(
      before
        .filter((entry, index) => JSON.stringify(entry) !== JSON.stringify(expected[index]))
        .map((entry) => entry.path),
      cached ? ['app/scripts/lint-web.mjs'] : [],
    );
    verifyExtraInputs(extras);

    // The capsule drains both streams after its storage bound, including on write failure.
    const capture = join(root, 'capture.mjs');
    writeFileSync(
      capture,
      String.raw`import { spawn } from 'node:child_process';
import { closeSync, openSync, readFileSync, writeFileSync, writeSync } from 'node:fs';

const spec = JSON.parse(readFileSync(process.argv[2], 'utf8'));
const bound = 128 * 1024;
const output = ['stdout', 'stderr'].map((name) => ({
  fd: openSync(spec[name], 'wx', 0o600),
  stored: 0, received: 0, exceeded: false, writeFault: false,
}));
let spawnFault = false;
let child;
let directChildClosed = false;
function capture(index, chunk) {
  const row = output[index];
  row.received += chunk.length;
  row.exceeded ||= row.received > bound;
  const selected = chunk.subarray(0, Math.max(0, bound - row.stored));
  if (!selected.length || row.writeFault) return;
  try {
    let offset = 0;
    while (offset < selected.length) {
      const written = writeSync(row.fd, selected, offset);
      if (written <= 0) throw new Error('capture write');
      offset += written;
      row.stored += written;
    }
  } catch { row.writeFault = true; }
}
let code = null;
let signal = null;
try {
  child = spawn(spec.command, spec.args, {
    cwd: spec.cwd, env: spec.environment, detached: false,
    stdio: ['ignore', 'pipe', 'pipe'],
  });
} catch { spawnFault = true; }
if (child) {
  const closed = new Promise((resolve) => child.once('close', (...values) => {
    directChildClosed = true;
    resolve(values);
  }));
  child.on('error', () => { spawnFault = true; });
  for (const [index, stream] of [child.stdout, child.stderr].entries()) {
    if (!stream) { output[index].writeFault = true; continue; }
    stream.on('data', (chunk) => capture(index, chunk));
    stream.on('error', () => { output[index].writeFault = true; });
  }
  [code, signal] = await closed;
}
for (const row of output) {
  try { closeSync(row.fd); } catch { row.writeFault = true; }
}
writeFileSync(spec.result, JSON.stringify({
  code, signal, spawnFault, directChildClosed,
  output: output.map(({ fd, ...row }) => row),
}) + '\n', { flag: 'wx', mode: 0o600 });
// Wrapper exit zero records its own completion only; the parent verifies the
// actual target result after the entire process family has physically retired.
`,
      { flag: 'wx', mode: 0o600 },
    );
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 150_000);
    try {
      for (const [stage, args] of [
        ['typecheck', ['-r', 'typecheck']],
        ['lint', ['-r', 'lint']],
      ]) {
        assert.equal(controller.signal.aborted, false);
        const specPath = join(root, `${stage}-spec.json`);
        const receiptPath = join(root, `${stage}-result.json`);
        const streams = [join(root, `${stage}-stdout`), join(root, `${stage}-stderr`)];
        writeFileSync(
          specPath,
          JSON.stringify({
            command: process.execPath,
            args: [manager.executable, ...args],
            cwd: copy.frontendRoot,
            environment,
            stdout: streams[0],
            stderr: streams[1],
            result: receiptPath,
          }) + '\n',
          { flag: 'wx', mode: 0o600 },
        );
        await runFrameworkProcessFamily(process.execPath, [capture, specPath], {
          cwd: copy.frontendRoot,
          environment,
          stdio: 'ignore',
          signal: controller.signal,
        });
        // The joined wrapper's zero is not the target's exit status.
        const receipt = JSON.parse(regularBytes(receiptPath).toString('utf8'));
        assert.equal(receipt.code, 0);
        assert.equal(receipt.signal, null);
        assert.equal(receipt.spawnFault, false);
        assert.equal(receipt.directChildClosed, true);
        assert.ok(Array.isArray(receipt.output) && receipt.output.length === 2);
        for (const [index, row] of receipt.output.entries()) {
          assert.equal(row.exceeded, false);
          assert.equal(row.writeFault, false);
          assert.ok(Number.isSafeInteger(row.received) && row.received >= 0);
          assert.equal(row.stored, row.received);
          assert.ok(row.stored <= 128 * 1024);
          const bytes = regularBytes(streams[index]);
          assert.equal(bytes.length, row.stored);
        }
      }
      assert.equal(controller.signal.aborted, false);
    } finally {
      clearTimeout(timer);
    }
    assertNoRetainedFrameworkOwners(root);
    const next = join(copy.appRoot, '.next');
    const files = snapshot(next);
    const cachePresent = files.some((entry) => entry.path.startsWith('cache/eslint/'));
    assert.equal(cachePresent, cached);
    if (!cached)
      assert.deepEqual(
        files.map((entry) => entry.path),
        ['types/routes.d.ts'],
      );
    const outputOwner = join(copy.appRoot, '.framework-state/output-next.json');
    assert.equal(existsSync(outputOwner), false);
    const lease = acquireFrameworkLease({ ...copy, kind: 'build' });
    let guardResult = 'accepted';
    try {
      try {
        cleanFrameworkOutputs(copy, { owner: lease.record });
      } catch (error) {
        if (!(error instanceof FrameworkAdmissionError) || error.reason !== 'output_unowned')
          throw error;
        guardResult = error.reason;
      }
    } finally {
      lease.release();
    }
    for (const path of [
      join(copy.appRoot, '.framework-state/writer.json'),
      join(copy.frontendRoot, 'packages/sdk/.framework-state/writer.json'),
      join(copy.frontendRoot, '.framework-state/readers', lease.record.nonce),
    ])
      assert.equal(existsSync(path), false);
    assertNoRetainedFrameworkOwners(root);
    assert.equal(guardResult, cached ? 'output_unowned' : 'accepted');
    if (cached) {
      assert.deepEqual(snapshot(next), files);
      assert.equal(existsSync(outputOwner), false);
    }
    assert.deepEqual(frameworkInputGraph(copy), before);
    assert.deepEqual(canonicalGraph(), expected);
    verifyExtraInputs(extras);
    safeToRemove = true;
    result = {
      commandsSucceeded: true,
      cachePresent,
      guardResult,
      sourcesUnchanged: true,
      familiesRetired: true,
      leasesRetired: true,
    };
  } finally {
    if (safeToRemove) rmSync(root, { recursive: true });
  }
  return { ...result, rootDisposed: !existsSync(root) };
}
