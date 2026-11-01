// Inert controls for the fixed copied-fixture reuse helper and its actual join caller.
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import vm from 'node:vm';
import { test } from 'node:test';

import { fixtureEnvironment } from '../apps/web/scripts/fixture-support.mjs';
import { stopTestChild } from '../apps/web/scripts/stop-test-child.mjs';

const web = realpathSync(fileURLToPath(new URL('../apps/web/', import.meta.url)));
const require = createRequire(join(web, 'package.json'));
const ts = require('typescript');
const helperSource = readFileSync(join(web, 'scripts/framework-proof-reuse.mjs'), 'utf8');
const verifierSource = readFileSync(join(web, 'scripts/verify-ingress.mjs'), 'utf8');
const file = ts.createSourceFile(
  'verify-ingress.mjs',
  verifierSource,
  ts.ScriptTarget.Latest,
  true,
  ts.ScriptKind.JS,
);
const declarations = file.statements.filter(
  (node) => ts.isFunctionDeclaration(node) && node.name?.text === 'reuseCopiedFramework',
);
assert.equal(declarations.length, 1);
const functionSource = declarations[0].getText(file);

function scope(body) {
  const root = realpathSync(mkdtempSync(join(realpathSync(tmpdir()), 'learnstack-reuse-fixture-')));
  const owner = realpathSync(
    mkdtempSync(join(realpathSync(tmpdir()), 'learnstack-ingress-controls-')),
  );
  const app = join(root, 'frontend/apps/web');
  const template = join(owner, 'prepared/frontend/apps/web');
  mkdirSync(join(app, 'scripts'), { recursive: true });
  mkdirSync(template, { recursive: true });
  writeFileSync(join(app, 'scripts/framework-proof-reuse.mjs'), helperSource);
  writeFileSync(
    join(app, 'scripts/framework-proof-support.mjs'),
    `export function reuseFrameworkProof({app,templateApp}) { ${body} }\n`,
  );
  return {
    root,
    owner,
    app,
    template,
    path: join(app, 'scripts/framework-proof-reuse.mjs'),
    dispose() {
      rmSync(root, { recursive: true, force: true });
      rmSync(owner, { recursive: true, force: true });
    },
  };
}
function execute(fixture, extra = {}, args = [fixture.app, fixture.template]) {
  return spawnSync(process.execPath, [fixture.path, ...args], {
    cwd: fixture.app,
    encoding: 'utf8',
    timeout: 5000,
    env: { ...fixtureEnvironment({ root: fixture.root }), ...extra },
  });
}
function closed(result, code) {
  assert.equal(result.error, undefined);
  assert.equal(result.signal, null);
  assert.equal(result.status, code);
  assert.equal(result.stdout, '');
  assert.equal(result.stderr, '');
}

test('fixed helper preserves true, false and refusal with no output', () => {
  for (const [body, code] of [
    ['return true;', 0],
    ['return false;', 10],
    ["throw new Error('PRIVATE-CANARY');", 1],
    ["return 'unexpected';", 1],
  ]) {
    const fixture = scope(body);
    try {
      closed(execute(fixture), code);
    } finally {
      fixture.dispose();
    }
  }
});

test('dirty helper environment and wrong arguments refuse without diagnostic payload', () => {
  const fixture = scope('return true;');
  try {
    closed(execute(fixture, { NODE_OPTIONS: '--no-warnings' }), 1);
    closed(execute(fixture, {}, [fixture.app]), 1);
    closed(execute(fixture, {}, [fixture.app, fixture.app]), 1);
    const env = fixtureEnvironment({ root: fixture.root });
    assert.equal(Object.hasOwn(env, 'NODE_OPTIONS'), false);
    const fixtureSupport = pathToFileURL(join(web, 'scripts/fixture-support.mjs')).href;
    const childCode = `import {fixtureEnvironment} from ${JSON.stringify(fixtureSupport)};
      const env=fixtureEnvironment({root:process.env.TMPDIR});
      process.stdout.write(JSON.stringify({parentPoisoned:process.env.NODE_OPTIONS==='--no-warnings',
        childOptions:Object.hasOwn(env,'NODE_OPTIONS'),childProxy:Object.hasOwn(env,'HTTP_PROXY'),tls:env.NODE_TLS_REJECT_UNAUTHORIZED}));`;
    const poisoned = spawnSync(process.execPath, ['--input-type=module', '-e', childCode], {
      encoding: 'utf8',
      timeout: 5000,
      env: {
        PATH: process.env.PATH,
        TMPDIR: fixture.root,
        NODE_OPTIONS: '--no-warnings',
        HTTP_PROXY: 'http://inert.invalid',
        NODE_TLS_REJECT_UNAUTHORIZED: '0',
      },
    });
    assert.equal(poisoned.status, 0);
    assert.equal(poisoned.signal, null);
    assert.equal(poisoned.stderr, '');
    assert.deepEqual(JSON.parse(poisoned.stdout), {
      parentPoisoned: true,
      childOptions: false,
      childProxy: false,
      tls: '1',
    });
  } finally {
    fixture.dispose();
  }
});

function caller(fixture, start, stop = stopTestChild) {
  return vm.runInNewContext(`(${functionSource})`, {
    app: fixture.app,
    frameworkTemplate: fixture.template,
    join,
    start,
    stopTestChild: stop,
    Promise,
    Error,
  });
}
function realStart(fixture, bodyOptions = {}) {
  return (argv, dev, observeBinding) => {
    assert.equal(dev, false);
    assert.equal(observeBinding, false);
    assert.deepEqual(Array.from(argv), [fixture.path, fixture.app, fixture.template]);
    return spawn(process.execPath, argv, {
      cwd: fixture.app,
      detached: true,
      stdio: ['ignore', 'pipe', 'pipe', 'ipc'],
      env: { ...fixtureEnvironment({ root: fixture.root }), ...bodyOptions },
    });
  };
}

test('actual caller refuses wrong outcome, unexpected output and forced join', async () => {
  for (const [body, stop] of [
    ['return false;', undefined],
    ["return 'unexpected';", undefined],
    ["process.stdout.write('PRIVATE-CANARY'); return true;", undefined],
    ['return true;', async () => ({ forced: true })],
  ]) {
    const fixture = scope(body);
    try {
      const action = caller(fixture, realStart(fixture), stop);
      if (body === 'return false;') assert.equal(await action(), false);
      else await assert.rejects(action(), { message: 'Fixture template reuse refused' });
    } finally {
      fixture.dispose();
    }
  }
});

test('actual caller waits for a real held descendant before accepting success', async () => {
  const fixture = scope('return true;');
  const release = join(fixture.root, 'release');
  const childCode = `const fs=require('node:fs');const t=setInterval(()=>{if(fs.existsSync(${JSON.stringify(release)})){clearInterval(t);process.exit(0)}},10);`;
  writeFileSync(
    join(fixture.app, 'scripts/framework-proof-support.mjs'),
    `import {spawn} from 'node:child_process'; export function reuseFrameworkProof(){ const child=spawn(process.execPath,['-e',${JSON.stringify(childCode)}],{stdio:'ignore'}); child.unref(); return true; }\n`,
  );
  let helper;
  let result;
  const start = (...args) => {
    helper = realStart(fixture)(...args);
    return helper;
  };
  try {
    let settled = false;
    result = caller(fixture, start)().then((value) => {
      settled = true;
      return value;
    });
    await new Promise((resolve, reject) => {
      helper.once('close', resolve);
      helper.once('error', reject);
    });
    await new Promise((resolve) => setTimeout(resolve, 80));
    assert.equal(settled, false, 'helper close alone cannot retire its real group');
    writeFileSync(release, '');
    assert.equal(await result, true);
  } finally {
    writeFileSync(release, '');
    let joined = false;
    if (result) {
      try {
        await result;
        joined = true;
      } catch {
        /* Retain uncertainty. */
      }
    }
    if (joined) fixture.dispose();
  }
});
