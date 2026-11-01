// Closed input preparation for disposable proof apps using the normal producer.
import assert from 'node:assert/strict';
import {
  copyFileSync,
  cpSync,
  existsSync,
  lstatSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  realpathSync,
  symlinkSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs';
import { join, resolve } from 'node:path';

import {
  copyQualifiedFrameworkBuild,
  frameworkInputGraph,
  verifyFrameworkBuild,
  verifyFrameworkInstallation,
} from '../../../scripts/framework-installation.mjs';

function inputFiles(root) {
  const entries = [];
  function visit(directory, prefix = '') {
    for (const entry of readdirSync(directory, { withFileTypes: true }).sort((a, b) =>
      a.name.localeCompare(b.name, 'en'),
    )) {
      assert.ok(!entry.isSymbolicLink(), 'SDK source aliases are not fixture inputs');
      const path = prefix ? prefix + '/' + entry.name : entry.name;
      if (entry.isDirectory()) visit(join(directory, entry.name), path);
      else {
        assert.ok(entry.isFile(), 'SDK inputs must be regular files');
        entries.push([path, readFileSync(join(directory, entry.name))]);
      }
    }
  }
  visit(root);
  return entries;
}

function copySDKInput(source, target) {
  assert.ok(!lstatSync(source).isSymbolicLink(), 'SDK source aliases are not fixture inputs');
  if (!existsSync(target)) cpSync(source, target, { recursive: true });
  if (lstatSync(source).isDirectory()) {
    assert.ok(
      lstatSync(target).isDirectory() && !lstatSync(target).isSymbolicLink(),
      'SDK input copy must be owned',
    );
    const before = inputFiles(source),
      after = inputFiles(target);
    assert.equal(after.length, before.length, 'SDK source membership must match');
    for (let index = 0; index < before.length; index++) {
      assert.equal(after[index][0], before[index][0], 'SDK source membership must match');
      assert.ok(after[index][1].equals(before[index][1]), 'SDK source bytes must match');
    }
  } else {
    assert.ok(
      lstatSync(source).isFile() &&
        lstatSync(target).isFile() &&
        !lstatSync(target).isSymbolicLink(),
      'SDK inputs must be regular files',
    );
    assert.ok(readFileSync(target).equals(readFileSync(source)), 'SDK source bytes must match');
  }
}

function dependencyLink(target, source) {
  if (existsSync(target)) {
    assert.ok(lstatSync(target).isSymbolicLink(), 'Fixture dependency must be an explicit link');
    assert.equal(
      realpathSync(target),
      realpathSync(source),
      'Fixture dependency resolution must match',
    );
  } else symlinkSync(source, target, 'dir');
}

function ownedDirectory(path) {
  if (!existsSync(path)) mkdirSync(path, { recursive: true });
  assert.ok(
    lstatSync(path).isDirectory() &&
      !lstatSync(path).isSymbolicLink() &&
      realpathSync(path) === path,
    'Fixture directory must be owned without aliases',
  );
}

function isolateSDK({ sourceApp, app }) {
  const sourceDependencies = join(sourceApp, 'node_modules');
  const sourceSDK = realpathSync(join(sourceDependencies, '@learnstack/sdk'));
  app = realpathSync(app);
  const sdk = resolve(app, '../../packages/sdk');
  assert.notEqual(sdk, sourceSDK, 'SDK output must be isolated');
  ownedDirectory(resolve(sdk, '..'));
  ownedDirectory(sdk);
  for (const name of ['src', 'scripts', 'package.json', 'tsconfig.json', 'tsconfig.native.json'])
    copySDKInput(join(sourceSDK, name), join(sdk, name));
  dependencyLink(join(sdk, 'node_modules'), join(sourceSDK, 'node_modules'));

  const dependencies = join(app, 'node_modules');
  if (existsSync(dependencies) && lstatSync(dependencies).isSymbolicLink()) {
    assert.equal(
      realpathSync(dependencies),
      realpathSync(sourceDependencies),
      'Only the declared dependency link can be replaced',
    );
    unlinkSync(dependencies); // Remove this owned link, never the dependency directory.
  }
  ownedDirectory(dependencies);
  ownedDirectory(join(dependencies, '@learnstack'));
  for (const name of readdirSync(sourceDependencies)) {
    if (name === '@learnstack') continue;
    dependencyLink(join(dependencies, name), join(sourceDependencies, name));
  }
  for (const name of readdirSync(join(sourceDependencies, '@learnstack')))
    dependencyLink(
      join(dependencies, '@learnstack', name),
      name === 'sdk' ? sdk : join(sourceDependencies, '@learnstack', name),
    );
  assert.deepEqual(
    readdirSync(dependencies).sort(),
    readdirSync(sourceDependencies).sort(),
    'Fixture dependency membership must match',
  );
  assert.deepEqual(
    readdirSync(join(dependencies, '@learnstack')).sort(),
    readdirSync(join(sourceDependencies, '@learnstack')).sort(),
    'Fixture workspace dependency membership must match',
  );
}

/** Link the qualified dependency guard; executable output is always produced in the copy. */
function replaceOnce(source, needle, replacement) {
  assert.equal(source.split(needle).length, 2, 'Copied proof source anchor must be unique');
  return source.replace(needle, replacement);
}

function bindCopiedProof(app, profile) {
  assert.ok(['none', 'ingress', 'admission', 'bff'].includes(profile), 'Closed copied profile');
  const ownerPath = join(app, 'scripts/public-server.mjs');
  let owner = readFileSync(ownerPath, 'utf8');
  owner = replaceOnce(owner, 'let lease;', 'let lease;\nlet copiedProof;');
  owner = replaceOnce(
    owner,
    '  prepareFrameworkNativeCache,',
    '  prepareFrameworkNativeCache,\n  frameworkLeaseSnapshot,',
  );
  owner = replaceOnce(
    owner,
    '  // Copied-proof owner instrumentation is inserted before generation.',
    `  // Copied-proof owner instrumentation is inserted before generation.
  const { createCopiedParentProof } = await import('./framework-proof-relay.mjs');
  copiedProof = createCopiedParentProof('${profile}');`,
  );
  owner = replaceOnce(
    owner,
    'buildFramework({ appRoot, mode, signal: startup.signal })',
    'buildFramework({ appRoot, mode, signal: startup.signal, onLease: copiedProof.onLease, onFamily: copiedProof.onProducerFamily })',
  );
  owner = replaceOnce(
    owner,
    '    identity = verifyFrameworkBuild({ appRoot }).identity;',
    '    identity = verifyFrameworkBuild({ appRoot }).identity;\n    copiedProof.onLease(frameworkLeaseSnapshot(identity, lease.record));',
  );
  owner = replaceOnce(
    owner,
    '    signal: startup.signal,\n  });',
    '    signal: startup.signal,\n    proof: copiedProof,\n    onFamily: copiedProof.onRuntimeFamily,\n  });',
  );
  owner = replaceOnce(
    owner,
    '  try {\n    lease?.release();',
    `  try {
    await copiedProof?.close();
    lease?.release();`,
  );
  writeFileSync(ownerPath, owner);

  const childPath = join(app, 'scripts/public-runtime-child.mjs');
  let child = readFileSync(childPath, 'utf8');
  child = replaceOnce(
    child,
    '  // Copied-proof child instrumentation is inserted after admission.',
    `  // Copied-proof child instrumentation is inserted after admission.
  const { createCopiedChildProof } = await import('./framework-proof-relay.mjs');`,
  );
  child = replaceOnce(
    child,
    '  const control = await child.admit(framework);',
    `  const control = await child.admit(framework, { proof: createCopiedChildProof('${profile}') });`,
  );
  if (profile === 'ingress')
    child = replaceOnce(
      child,
      '  if (control) {\n    const { startPublicRuntime }',
      `  if (control) {
    const { installCopiedIngressBinding } = await import('./framework-proof-ingress-bind.mjs');
    installCopiedIngressBinding(control.proof);
    const { startPublicRuntime }`,
    );
  writeFileSync(childPath, child);
}

export function prepareFrameworkProof({ sourceApp, app, profile = 'none' }) {
  const dependencyScripts = resolve(sourceApp, '../../scripts');
  const fixtureScripts = resolve(app, '../../scripts');
  assert.notEqual(fixtureScripts, dependencyScripts, 'Proof output must be isolated');
  mkdirSync(resolve(fixtureScripts, '..'), { recursive: true });
  assert.notEqual(
    realpathSync(resolve(fixtureScripts, '..')),
    realpathSync(resolve(dependencyScripts, '..')),
    'Proof output must be isolated',
  );
  if (existsSync(fixtureScripts)) {
    assert.ok(lstatSync(fixtureScripts).isSymbolicLink(), 'Proof guard link must be explicit');
    assert.equal(realpathSync(fixtureScripts), realpathSync(dependencyScripts));
  } else symlinkSync(dependencyScripts, fixtureScripts, 'dir');
  mkdirSync(join(app, 'scripts'), { recursive: true });
  isolateSDK({ sourceApp, app });
  for (const name of [
    'framework-build.mjs',
    'public-server.mjs',
    'public-runtime-child.mjs',
    'framework-proof-relay.mjs',
    'framework-proof-ingress-bind.mjs',
    'ensure-routes-stub.mjs',
  ])
    copyFileSync(join(sourceApp, 'scripts', name), join(app, 'scripts', name));
  bindCopiedProof(app, profile);
}

/** The supported producer verifies actual manager/package inputs before generation. */
export function frameworkProofBuildArguments(app) {
  return [join(app, 'scripts/framework-build.mjs'), '--app-root', app];
}

/** Reuse only an intact producer artifact whose actual copied input graph is identical. */
export function reuseFrameworkProof({ app, templateApp }) {
  const prepared = verifyFrameworkBuild({ appRoot: templateApp });
  const actual = verifyFrameworkInstallation({ appRoot: app });
  if (JSON.stringify(frameworkInputGraph(actual)) !== JSON.stringify(prepared.manifest.inputs))
    return false; // Changed input gets a real clean producer; never a re-labelled manifest.
  // The normal verified-copy boundary holds source and target leases, checks
  // fresh output ownership and preserves the original producer manifest.
  copyQualifiedFrameworkBuild({ sourceAppRoot: templateApp, targetAppRoot: app });
  return true;
}

export { copyQualifiedFrameworkBuild };
