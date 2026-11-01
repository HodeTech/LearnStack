import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { chmodSync, existsSync, mkdtempSync, readFileSync, realpathSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';

import { createQualifiedPrivateFrameworkSource } from '../apps/web/scripts/framework-private-source.mjs';
import { frameworkInputGraph } from './framework-installation.mjs';

const frontend = realpathSync(new URL('../', import.meta.url));
const app = join(frontend, 'apps/web');
const fixture = () => realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-framework-ownership-')));
const digest = (bytes) => createHash('sha256').update(bytes).digest('hex');

test('declared qualified source graph copies byte-for-byte into an isolated frontend', () => {
  const root = fixture();
  try {
    const source = createQualifiedPrivateFrameworkSource({ root });
    assert.equal(source.frontendRoot, join(root, 'private-source/frontend'));
    assert.equal(source.appRoot, join(source.frontendRoot, 'apps/web'));
    assert.equal(
      source.driverPath,
      join(source.appRoot, 'scripts/verify-framework-runtime-ownership.mjs'),
    );
    assert.equal(source.guardPath, join(source.frontendRoot, 'scripts/framework-installation.mjs'));
    assert.equal(
      source.brokerPath,
      join(source.appRoot, 'scripts/framework-proof-host-broker.mjs'),
    );
    assert.deepEqual(
      source.inventory,
      frameworkInputGraph({ appRoot: app, frontendRoot: frontend }),
    );
    assert.equal(source.inputSha256, digest(Buffer.from(JSON.stringify(source.inventory))));
    assert.equal(
      source.totalBytes,
      source.inventory.reduce((sum, subject) => sum + subject.bytes, 0),
    );
    assert.ok(Object.isFrozen(source) && Object.isFrozen(source.inventory));
    assert.ok(source.inventory.every((subject) => Object.isFrozen(subject)));
    assert.ok(
      readFileSync(source.driverPath).equals(
        readFileSync(join(app, 'scripts/verify-framework-runtime-ownership.mjs')),
      ),
    );
    for (const path of ['.next', '.server', '.framework-state', '.env.local', 'src/.env.local'])
      assert.equal(existsSync(join(source.appRoot, path)), false, path);
    assert.equal(existsSync(join(source.frontendRoot, '.framework-state')), false);
    assert.equal(existsSync(join(source.frontendRoot, '.git')), false);
    assert.equal(existsSync(join(source.frontendRoot, '.claude')), false);
    assert.equal(existsSync(join(source.frontendRoot, 'packages/sdk/dist')), false);
    assert.equal(
      realpathSync(join(source.appRoot, 'node_modules/@learnstack/sdk')),
      join(source.frontendRoot, 'packages/sdk'),
    );
    for (const pkg of ['ui', 'config'])
      assert.equal(
        realpathSync(join(source.appRoot, 'node_modules/@learnstack', pkg)),
        join(source.frontendRoot, 'packages', pkg),
      );
    assert.equal(
      realpathSync(join(source.appRoot, 'node_modules/next')),
      realpathSync(join(app, 'node_modules/next')),
    );
    for (const pkg of ['sdk', 'ui'])
      assert.equal(
        realpathSync(join(source.frontendRoot, 'packages', pkg, 'node_modules/@learnstack/config')),
        join(source.frontendRoot, 'packages/config'),
      );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('the copied guard and broker import from the private source root', () => {
  const root = fixture();
  try {
    const source = createQualifiedPrivateFrameworkSource({ root });
    const probe = `
      import { FRAMEWORK_FRONTEND_ROOT, verifyFrameworkInstallation } from './scripts/framework-installation.mjs';
      import { createCopiedRuntimeHostBroker } from './apps/web/scripts/framework-proof-host-broker.mjs';
      const identity = verifyFrameworkInstallation({
        appRoot: process.cwd() + '/apps/web', frontendRoot: process.cwd() });
      process.stdout.write(JSON.stringify({ root: FRAMEWORK_FRONTEND_ROOT, app: identity.appRoot,
        broker: typeof createCopiedRuntimeHostBroker }) + '\\n');
    `;
    const observed = JSON.parse(
      execFileSync(process.execPath, ['--input-type=module', '-e', probe], {
        cwd: source.frontendRoot,
        encoding: 'utf8',
        timeout: 5000,
        maxBuffer: 4096,
      }),
    );
    assert.deepEqual(observed, {
      root: source.frontendRoot,
      app: source.appRoot,
      broker: 'function',
    });
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('private source scope refuses non-private roots and cannot merge with an earlier copy', () => {
  const root = fixture();
  try {
    chmodSync(root, 0o755);
    assert.throws(() => createQualifiedPrivateFrameworkSource({ root }));
    chmodSync(root, 0o700);
    createQualifiedPrivateFrameworkSource({ root });
    assert.throws(() => createQualifiedPrivateFrameworkSource({ root }));
  } finally {
    chmodSync(root, 0o700);
    rmSync(root, { recursive: true, force: true });
  }
});
