import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import {
  chmodSync,
  lstatSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { createServer } from 'node:https';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';

import { installCopiedExternalProviderCa } from '../apps/web/scripts/framework-external-provider-ca-source.mjs';
import { createFixtureOwner } from '../apps/web/scripts/fixture-support.mjs';
import { prepareFrameworkProof } from '../apps/web/scripts/framework-proof-support.mjs';

const sourceApp = realpathSync(fileURLToPath(new URL('../apps/web/', import.meta.url)));
const run = (owner, program, env) =>
  new Promise((resolve, reject) => {
    const child = owner.ownChild(
      spawn(process.execPath, ['--input-type=module', '-e', program], {
        env,
        detached: process.platform !== 'win32',
        stdio: ['ignore', 'pipe', 'pipe'],
      }),
    );
    let stdout = '';
    let stderr = '';
    let overflow = false;
    const append = (key, part) => {
      if (
        Buffer.byteLength(part) > 2048 ||
        Buffer.byteLength(key === 'stdout' ? stdout : stderr) + Buffer.byteLength(part) > 2048
      ) {
        overflow = true;
        if (child.exitCode === null && child.signalCode === null) child.kill('SIGTERM');
      } else if (key === 'stdout') stdout += part;
      else stderr += part;
    };
    child.stdout.setEncoding('utf8').on('data', (part) => append('stdout', part));
    child.stderr.setEncoding('utf8').on('data', (part) => append('stderr', part));
    child.once('error', reject);
    child.once('close', (code, signal) =>
      overflow
        ? reject(new Error('Bounded child output refused'))
        : resolve({ code, signal, stdout, stderr }),
    );
  });

async function withOwnedRoot(prefix, action) {
  const owner = createFixtureOwner();
  owner.install();
  const root = owner.ownRoot(realpathSync(mkdtempSync(join(tmpdir(), prefix))));
  let result;
  let failure;
  try {
    result = await action({ owner, root });
  } catch (error) {
    failure = error;
  }
  try {
    await owner.dispose();
  } catch (error) {
    if (failure) throw new AggregateError([failure, error], 'Owned CA control and cleanup failed');
    throw error;
  }
  if (failure) throw failure;
  return result;
}

function cert(root, name) {
  const key = join(root, `${name}.key`);
  const pem = join(root, `${name}.pem`);
  execFileSync(
    'openssl',
    [
      'req',
      '-x509',
      '-newkey',
      'rsa:2048',
      '-nodes',
      '-keyout',
      key,
      '-out',
      pem,
      '-days',
      '1',
      '-subj',
      '/CN=127.0.0.1',
      '-addext',
      'subjectAltName=IP:127.0.0.1',
    ],
    { stdio: 'ignore' },
  );
  return { key, pem };
}

test('external-only copied owner binds exact private CA before the build', () =>
  withOwnedRoot('learnstack-provider-ca-source-', async ({ owner, root }) => {
    const app = join(root, 'frontend/apps/web');
    prepareFrameworkProof({ sourceApp, app, profile: 'none' });
    const good = cert(root, 'good');
    const authority = readFileSync(good.pem, 'utf8');
    const bound = installCopiedExternalProviderCa({ root, app, authority });
    const stat = lstatSync(bound.caPath);
    assert.equal(stat.mode & 0o777, 0o600);
    assert.equal(stat.nlink, 1);
    assert.equal(stat.uid, process.getuid());
    const ownerSource = readFileSync(bound.ownerPath, 'utf8');
    assert.equal(ownerSource.includes('environment.NODE_EXTRA_CA_CERTS = caPath;'), true);
    assert.equal(ownerSource.includes(`!== '${bound.sha256}'`), true);
    assert.equal(ownerSource.includes('named.ino !== opened.ino'), true);
    assert.equal(ownerSource.includes('named.mtimeNs !== opened.mtimeNs'), true);
    assert.equal(
      ownerSource.indexOf('environment.NODE_EXTRA_CA_CERTS = caPath;') <
        ownerSource.indexOf('const receipt = await runFrameworkRuntimeFamily'),
      true,
    );
    execFileSync(process.execPath, ['--check', bound.ownerPath]);
    assert.throws(() => installCopiedExternalProviderCa({ root, app, authority }), /EEXIST/);

    // This exact generated admission block is run in an inert Node child; it
    // does not start the framework or touch production trust configuration.
    const block = ownerSource
      .split('  const environment = frameworkRuntimeEnvironment(identity, mode);')[1]
      ?.split('  const receipt = await runFrameworkRuntimeFamily')[0];
    assert.ok(block?.includes('environment.NODE_EXTRA_CA_CERTS = caPath;'));
    const probe = `${ownerSource.match(/^import \{ createHash \} from 'node:crypto';/m)?.[0] ?? ''}
import { closeSync, constants, fstatSync, lstatSync, openSync, readFileSync, realpathSync } from 'node:fs';
import { join, resolve } from 'node:path';
const appRoot = ${JSON.stringify(realpathSync(app))};
const environment = {};
try { ${block} console.log(environment.NODE_EXTRA_CA_CERTS === ${JSON.stringify(bound.caPath)} ? 'accepted' : 'refused'); }
catch { console.log('refused'); }`;
    const minimalEnv = { PATH: process.env.PATH };
    const accepted = await run(owner, probe, minimalEnv);
    assert.deepEqual(
      [accepted.code, accepted.signal, accepted.stdout.trim()],
      [0, null, 'accepted'],
    );
    assert.equal(accepted.stderr, '');
    const swappedProbe = `import fs from 'node:fs';
import { syncBuiltinESMExports } from 'node:module';
${probe
  .replace(
    'const environment = {};',
    `const environment = {};
let swapped = false;
const originalRead = fs.readFileSync;
fs.readFileSync = (source, ...args) => {
  const bytes = originalRead(source, ...args);
  if (typeof source === 'number' && !swapped) {
    swapped = true;
    fs.renameSync(${JSON.stringify(bound.caPath)}, ${JSON.stringify(bound.caPath + '.retired')});
    fs.writeFileSync(${JSON.stringify(bound.caPath)}, bytes, { flag: 'wx', mode: 0o600 });
  }
  return bytes;
};
syncBuiltinESMExports();`,
  )
  .replace(
    "catch { console.log('refused'); }",
    "catch { console.log(swapped ? 'refused-swapped' : 'refused-unswapped'); }",
  )}`;
    const replacedDuringRead = await run(owner, swappedProbe, minimalEnv);
    assert.deepEqual(
      [replacedDuringRead.code, replacedDuringRead.signal, replacedDuringRead.stdout.trim()],
      [0, null, 'refused-swapped'],
    );
    assert.equal(replacedDuringRead.stderr, '');
    writeFileSync(bound.caPath, 'tampered');
    const dirty = await run(owner, probe, minimalEnv);
    assert.deepEqual([dirty.code, dirty.signal, dirty.stdout.trim()], [0, null, 'refused']);
    assert.equal(dirty.stderr, '');
    writeFileSync(bound.caPath, authority);
    chmodSync(bound.caPath, 0o644);
    const mode = await run(owner, probe, minimalEnv);
    assert.equal(mode.stdout.trim(), 'refused');
    assert.equal(mode.stderr, '');
    chmodSync(bound.caPath, 0o600);
    rmSync(bound.caPath);
    symlinkSync(good.pem, bound.caPath);
    const alias = await run(owner, probe, minimalEnv);
    assert.equal(alias.stdout.trim(), 'refused');
    assert.equal(alias.stderr, '');
  }));

test('copied trust transform refuses other profiles and changed source anchors', async () => {
  for (const change of [
    'bff-profile',
    'bff-plus-fake-none-comment',
    'missing-anchor',
    'duplicate-anchor',
  ]) {
    await withOwnedRoot('learnstack-provider-ca-refusal-', async ({ root }) => {
      const app = join(root, 'frontend/apps/web');
      prepareFrameworkProof({
        sourceApp,
        app,
        profile: change.startsWith('bff') ? 'bff' : 'none',
      });
      const ownerPath = join(app, 'scripts/public-server.mjs');
      if (change === 'bff-plus-fake-none-comment') {
        const source = readFileSync(ownerPath, 'utf8');
        writeFileSync(ownerPath, source + "\n// copiedProof = createCopiedParentProof('none');\n");
      } else if (change !== 'bff-profile') {
        const source = readFileSync(ownerPath, 'utf8');
        const anchor = '  const environment = frameworkRuntimeEnvironment(identity, mode);';
        writeFileSync(
          ownerPath,
          change === 'missing-anchor'
            ? source.replace(anchor, '  const environment = {};')
            : source.replace(anchor, anchor + '\n' + anchor),
        );
      }
      const good = cert(root, 'good');
      const authority = readFileSync(good.pem, 'utf8');
      assert.throws(() => installCopiedExternalProviderCa({ root, app, authority }));
    });
  }
});

test('actual child Node TLS trusts only the exact startup CA', () =>
  withOwnedRoot('learnstack-provider-ca-tls-', async ({ owner, root }) => {
    const good = cert(root, 'good');
    const wrong = cert(root, 'wrong');
    const server = owner.ownServer(
      createServer(
        { key: readFileSync(good.key), cert: readFileSync(good.pem) },
        (_request, response) => response.end('ok'),
      ),
    );
    await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
    const origin = `https://127.0.0.1:${server.address().port}/`;
    const program = `delete process.env.NODE_EXTRA_CA_CERTS;
try { const response = await fetch(${JSON.stringify(origin)}); console.log(response.ok && await response.text() === 'ok' ? 'trusted' : 'rejected'); }
catch { console.log('rejected'); }`;
    const base = { PATH: process.env.PATH, NODE_TLS_REJECT_UNAUTHORIZED: '1' };
    const [correct, missing, incorrect] = await Promise.all([
      run(owner, program, { ...base, NODE_EXTRA_CA_CERTS: good.pem }),
      run(owner, program, base),
      run(owner, program, { ...base, NODE_EXTRA_CA_CERTS: wrong.pem }),
    ]);
    assert.deepEqual([correct.code, correct.signal, correct.stdout.trim()], [0, null, 'trusted']);
    assert.deepEqual([missing.code, missing.signal, missing.stdout.trim()], [0, null, 'rejected']);
    assert.deepEqual(
      [incorrect.code, incorrect.signal, incorrect.stdout.trim()],
      [0, null, 'rejected'],
    );
    for (const result of [correct, missing, incorrect]) assert.equal(result.stderr, '');
  }));

test('random-listener external owner uses only the explicitly selected existing bff relay', () =>
  withOwnedRoot('learnstack-provider-ca-listener-', async ({ root }) => {
    const app = join(root, 'frontend/apps/web');
    prepareFrameworkProof({ sourceApp, app, profile: 'bff' });
    const good = cert(root, 'good');
    const authority = readFileSync(good.pem, 'utf8');
    const bound = installCopiedExternalProviderCa({ root, app, authority, randomListener: true });
    const source = readFileSync(bound.ownerPath, 'utf8');
    assert.equal(source.includes("copiedProof = createCopiedParentProof('bff');"), true);
    assert.equal(source.includes('environment.NODE_EXTRA_CA_CERTS = caPath;'), true);
    assert.equal(source.includes(`!== '${bound.sha256}'`), true);
    execFileSync(process.execPath, ['--check', bound.ownerPath]);
  }));
