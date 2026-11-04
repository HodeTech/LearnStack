// A private copy of the guard's declared source graph for fatal ownership proofs.
// The ordinary BFF fixture builder remains the sole executable-app builder.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import {
  copyFileSync,
  lstatSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  realpathSync,
  symlinkSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';

import {
  FRAMEWORK_FRONTEND_ROOT,
  frameworkInputGraph,
  verifyFrameworkInstallation,
} from '../../../scripts/framework-installation.mjs';

const sourceApp = realpathSync(new URL('../', import.meta.url));
const sourceFrontend = realpathSync(new URL('../../../', import.meta.url));
const workspacePackages = ['sdk', 'ui', 'config'];
const rootFiles = [
  'package.json',
  'pnpm-lock.yaml',
  'pnpm-workspace.yaml',
  '.npmrc',
  '.pnpmfile.cjs',
  '.nvmrc',
];
const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex');
const inside = (path, root) => {
  const part = relative(root, path);
  return part === '' || (part !== '..' && !part.startsWith(`..${sep}`) && !isAbsolute(part));
};
const ownDirectory = (path) => {
  const entry = lstatSync(path);
  assert.ok(entry.isDirectory() && !entry.isSymbolicLink() && realpathSync(path) === path);
};

function qualifiedRoot(root) {
  assert.equal(typeof root, 'string', 'private source root is a fixture-owned path');
  assert.ok(isAbsolute(root));
  root = resolve(root);
  ownDirectory(root);
  const entry = lstatSync(root);
  assert.equal(entry.uid, process.getuid());
  assert.equal(entry.mode & 0o777, 0o700);
  assert.ok(inside(root, realpathSync(tmpdir())));
  assert.ok(basename(root).startsWith('learnstack-framework-ownership-'));
  return root;
}

function sourcePaths(path) {
  const [subject, ...rest] = path.split('/');
  const name = rest.join('/');
  assert.ok(name && !rest.includes('..') && !rest.includes('.'));
  switch (subject) {
    case 'app':
      return [join(sourceApp, name), join('apps/web', name)];
    case 'workspace':
      return [join(sourceFrontend, name), name];
    case 'sdk':
    case 'ui':
    case 'config':
      return [join(sourceFrontend, 'packages', subject, name), join('packages', subject, name)];
    default:
      throw new Error('Private framework source graph refused an undeclared subject');
  }
}

function linkQualifiedPackage(target, original, store) {
  const entry = lstatSync(original);
  assert.ok(entry.isSymbolicLink(), 'installed package must be a qualified direct link');
  const resolved = realpathSync(original);
  assert.ok(inside(resolved, store), 'installed dependency leaves the qualified store');
  symlinkSync(resolved, target, 'dir');
  assert.equal(realpathSync(target), resolved);
}

function mirrorDependencies(original, copied, store, frontendRoot) {
  mkdirSync(copied);
  const names = readdirSync(original).filter((name) => !name.startsWith('.') || name === '.bin');
  for (const name of names) {
    if (name === '.bin') {
      ownDirectory(join(original, name));
      symlinkSync(join(original, name), join(copied, name), 'dir');
    } else if (name === '@learnstack') {
      mkdirSync(join(copied, name));
      const linked = readdirSync(join(original, name)).sort();
      assert.ok(linked.length > 0 && linked.every((pkg) => workspacePackages.includes(pkg)));
      for (const pkg of linked) {
        const originalPackage = realpathSync(join(original, name, pkg));
        assert.equal(originalPackage, join(sourceFrontend, 'packages', pkg));
        symlinkSync(join(frontendRoot, 'packages', pkg), join(copied, name, pkg), 'dir');
      }
    } else if (name.startsWith('@')) {
      ownDirectory(join(original, name));
      mkdirSync(join(copied, name));
      for (const pkg of readdirSync(join(original, name)))
        linkQualifiedPackage(join(copied, name, pkg), join(original, name, pkg), store);
    } else linkQualifiedPackage(join(copied, name), join(original, name), store);
  }
}

function linkSourceDependencies(frontendRoot, appRoot) {
  const installed = join(sourceFrontend, 'node_modules');
  const store = realpathSync(installed);
  assert.equal(
    realpathSync(join(sourceApp, 'node_modules/next')),
    realpathSync(join(installed, '.pnpm/node_modules/next')),
  );
  symlinkSync(store, join(frontendRoot, 'node_modules'), 'dir');
  mirrorDependencies(
    join(sourceApp, 'node_modules'),
    join(appRoot, 'node_modules'),
    store,
    frontendRoot,
  );
  for (const pkg of workspacePackages) {
    const originalDependencies = join(sourceFrontend, 'packages', pkg, 'node_modules');
    const privateDependencies = join(frontendRoot, 'packages', pkg, 'node_modules');
    ownDirectory(originalDependencies);
    mirrorDependencies(originalDependencies, privateDependencies, store, frontendRoot);
  }
  assert.equal(
    realpathSync(join(appRoot, 'node_modules/@learnstack/sdk')),
    join(frontendRoot, 'packages/sdk'),
  );
  assert.equal(
    realpathSync(join(appRoot, 'node_modules/next')),
    realpathSync(join(sourceApp, 'node_modules/next')),
  );
  for (const pkg of ['sdk', 'ui'])
    assert.equal(
      realpathSync(join(frontendRoot, 'packages', pkg, 'node_modules/@learnstack/config')),
      join(frontendRoot, 'packages/config'),
    );
}

/** Copy only the current guard's declared source inputs into an owned sibling scope. */
export function createQualifiedPrivateFrameworkSource({ root }) {
  assert.equal(FRAMEWORK_FRONTEND_ROOT, sourceFrontend);
  root = qualifiedRoot(root);
  const privateScope = join(root, 'private-source');
  mkdirSync(privateScope, { mode: 0o700 }); // Exclusive: never merge with a stale scope.
  const frontendRoot = join(privateScope, 'frontend');
  const appRoot = join(frontendRoot, 'apps/web');
  mkdirSync(frontendRoot, { mode: 0o700 });
  const expected = frameworkInputGraph({ appRoot: sourceApp, frontendRoot: sourceFrontend });
  assert.ok(expected.length > 0);
  const inventory = [];
  for (const subject of expected) {
    const [original, suffix] = sourcePaths(subject.path);
    assert.equal(realpathSync(original), original, 'source graph input must have no alias');
    const bytes = readFileSync(original);
    assert.equal(bytes.length, subject.bytes);
    assert.equal(sha256(bytes), subject.sha256);
    const target = join(frontendRoot, suffix);
    mkdirSync(dirname(target), { recursive: true, mode: 0o700 });
    copyFileSync(original, target);
    const copied = readFileSync(target);
    assert.ok(copied.equals(bytes), 'private source bytes must equal the qualified input');
    inventory.push(
      Object.freeze({ path: subject.path, bytes: subject.bytes, sha256: subject.sha256 }),
    );
  }
  assert.ok(
    rootFiles.every((name) => inventory.some((entry) => entry.path === `workspace/${name}`)),
  );
  linkSourceDependencies(frontendRoot, appRoot);
  const actual = frameworkInputGraph({ appRoot, frontendRoot });
  assert.deepEqual(actual, expected, 'private input graph must exactly match the qualified source');
  const installation = verifyFrameworkInstallation({ appRoot, frontendRoot });
  assert.equal(installation.frontendRoot, frontendRoot);
  assert.equal(installation.appRoot, appRoot);
  const driverPath = join(appRoot, 'scripts/verify-framework-runtime-ownership.mjs');
  const guardPath = join(frontendRoot, 'scripts/framework-installation.mjs');
  const brokerPath = join(appRoot, 'scripts/framework-proof-host-broker.mjs');
  const helperPath = join(appRoot, 'scripts/framework-private-source.mjs');
  for (const path of [driverPath, guardPath, brokerPath, helperPath]) {
    const entry = lstatSync(path);
    assert.ok(entry.isFile() && !entry.isSymbolicLink());
  }
  const frozenInventory = Object.freeze(inventory);
  return Object.freeze({
    frontendRoot,
    appRoot,
    driverPath,
    guardPath,
    brokerPath,
    inventory: frozenInventory,
    inputSha256: sha256(Buffer.from(JSON.stringify(frozenInventory))),
    totalBytes: frozenInventory.reduce((sum, entry) => sum + entry.bytes, 0),
  });
}
