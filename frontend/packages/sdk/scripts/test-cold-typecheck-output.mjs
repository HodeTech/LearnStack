import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import {
  constants,
  copyFileSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { createQualifiedPrivateFrameworkSource } from '../../../apps/web/scripts/framework-private-source.mjs';
import {
  acquireFrameworkLease,
  cleanFrameworkOutputs,
  frameworkInputGraph,
  FrameworkAdmissionError,
} from '../../../scripts/framework-installation.mjs';
import { runFrameworkProcessFamily } from '../../../scripts/framework-process-family.mjs';

const frontendRoot = fileURLToPath(new URL('../../../', import.meta.url));
const appRoot = join(frontendRoot, 'apps/web');
const contract = fileURLToPath(new URL('../../../../backend/openapi/v1.json', import.meta.url));
const hash = (bytes) => createHash('sha256').update(bytes).digest('hex');
const canonicalGraph = () => frameworkInputGraph({ appRoot, frontendRoot });

/** Exercise the actual compiler and native output guard in a private cold source copy. */
export async function checkColdTypecheckOutput(incremental) {
  assert.equal(typeof incremental, 'boolean');
  const expected = canonicalGraph();
  const contractHash = hash(readFileSync(contract));
  const root = mkdtempSync(
    join(realpathSync(tmpdir()), 'learnstack-framework-ownership-cold-sdk-'),
  );
  let safeToRemove = true;
  let result;
  try {
    const copy = createQualifiedPrivateFrameworkSource({ root });
    assert.deepEqual(copy.inventory, expected);
    for (const name of [
      'sdk/scripts/test-cold-typecheck-output.mjs',
      'sdk/src/cold-typecheck-output.test.ts',
    ]) {
      assert.ok(copy.inventory.some((entry) => entry.path === name));
    }
    const copiedContract = join(dirname(copy.frontendRoot), 'backend/openapi/v1.json');
    mkdirSync(dirname(copiedContract), { recursive: true, mode: 0o700 });
    copyFileSync(contract, copiedContract, constants.COPYFILE_EXCL);
    assert.equal(hash(readFileSync(copiedContract)), contractHash);

    const sdk = join(copy.frontendRoot, 'packages/sdk');
    const configPath = join(sdk, 'tsconfig.json');
    const config = JSON.parse(readFileSync(configPath, 'utf8'));
    assert.equal(config.compilerOptions.incremental, false);
    if (incremental) {
      config.compilerOptions.incremental = true;
      writeFileSync(configPath, `${JSON.stringify(config, null, 2)}\n`);
    }
    const before = frameworkInputGraph(copy);
    assert.equal(before.length, expected.length);
    const changedSources = before
      .filter((entry, index) => JSON.stringify(entry) !== JSON.stringify(expected[index]))
      .map((entry) => entry.path);
    assert.deepEqual(changedSources, incremental ? ['sdk/tsconfig.json'] : []);

    const dist = join(sdk, 'dist');
    const info = join(dist, 'tsconfig.tsbuildinfo');
    const owner = join(sdk, '.framework-state/output-dist.json');
    assert.equal(existsSync(dist), false);
    assert.equal(existsSync(owner), false);
    const tsc = createRequire(join(sdk, 'package.json')).resolve('typescript/bin/tsc');
    const control = new AbortController();
    const timer = setTimeout(() => control.abort(), 60_000);
    safeToRemove = false;
    try {
      await runFrameworkProcessFamily(process.execPath, [tsc, '--noEmit'], {
        cwd: sdk,
        environment: { CI: 'true' },
        stdio: 'inherit',
        signal: control.signal,
      });
      safeToRemove = true;
    } finally {
      clearTimeout(timer);
    }
    assert.equal(control.signal.aborted, false);
    assert.equal(existsSync(dist), incremental);
    assert.equal(existsSync(info), incremental);
    assert.equal(existsSync(owner), false);
    const infoHash = incremental ? hash(readFileSync(info)) : null;

    safeToRemove = false;
    const lease = acquireFrameworkLease({ ...copy, kind: 'native' });
    let guardResult = 'accepted';
    try {
      try {
        cleanFrameworkOutputs(copy, { nativeOnly: true, owner: lease.record });
      } catch (error) {
        if (!(error instanceof FrameworkAdmissionError) || error.reason !== 'output_unowned')
          throw error;
        guardResult = error.reason;
      }
    } finally {
      lease.release();
      assert.equal(existsSync(join(copy.appRoot, '.framework-state/writer.json')), false);
      assert.equal(existsSync(join(sdk, '.framework-state/writer.json')), false);
      assert.equal(
        existsSync(join(copy.frontendRoot, '.framework-state/readers', lease.record.nonce)),
        false,
      );
      safeToRemove = true;
    }
    assert.equal(guardResult, incremental ? 'output_unowned' : 'accepted');
    if (incremental) assert.equal(hash(readFileSync(info)), infoHash);
    assert.deepEqual(frameworkInputGraph(copy), before);
    assert.deepEqual(canonicalGraph(), expected);
    assert.equal(hash(readFileSync(contract)), contractHash);
    result = {
      compilerSucceeded: true,
      buildInfoPresent: incremental,
      guardResult,
      sourcesUnchanged: true,
      leasesRetired: true,
    };
  } finally {
    if (safeToRemove) rmSync(root, { recursive: true });
  }
  return { ...result, rootDisposed: !existsSync(root) };
}
