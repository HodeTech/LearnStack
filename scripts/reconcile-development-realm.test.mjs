import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { realmFailure } from './development-realm-errors.mjs';
import { reconcileDevelopmentRealm } from './reconcile-development-realm.mjs';
import { repositoryRoot } from './compose.mjs';

const canary = 'synthetic-private-cli-canary-73f2';
const entry = fileURLToPath(new URL('./reconcile-development-realm.mjs', import.meta.url));

async function cli(args, environment = {}) {
  const child = spawn(process.execPath, [entry, ...args], {
    // No executable is available to perform Docker inspection or Seeder work.
    env: { PATH: '', KEYCLOAK_ADMIN_PASSWORD: canary, KEYCLOAK_WEB_CLIENT_SECRET: canary, ...environment },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  const out = [];
  const err = [];
  child.stdout.on('data', value => out.push(value));
  child.stderr.on('data', value => err.push(value));
  const timer = setTimeout(() => child.kill('SIGKILL'), 5000);
  try {
    const status = await new Promise((resolve, reject) => {
      child.once('error', reject);
      child.once('close', (code, signal) => resolve({ code, signal }));
    });
    return { ...status, stdout: Buffer.concat(out).toString(), stderr: Buffer.concat(err).toString() };
  } finally { clearTimeout(timer); }
}

for (const args of [
  ['--help'], ['--url', `https://${canary}.invalid/`], ['--realm', canary],
  ['--import', canary], ['--reset'], ['--response-limit', '1048576'],
]) {
  test(`the closed CLI rejects ${args[0]} before selecting or authenticating a target`, async () => {
    const result = await cli(args);
    assert.equal(result.code, 1);
    assert.equal(result.signal, null);
    assert.equal(result.stdout, '');
    assert.equal(result.stderr, 'realm: preflight/configuration\n');
    assert.ok(!result.stderr.includes(canary));
  });
}

test('an older private file is refused with a closed missing-secret reason before any command', async t => {
  const dir = await mkdtemp(join(tmpdir(), 'learnstack-realm-entry-'));
  t.after(() => rm(dir, { recursive: true, force: true }));
  const file = join(dir, '.env');
  const source = `KEYCLOAK_ADMIN=synthetic-admin\nKEYCLOAK_ADMIN_PASSWORD=${canary}\n`;
  await writeFile(file, source, { mode: 0o600 });
  const result = await cli([], { LEARNSTACK_COMPOSE_ENV_FILE: file });
  assert.equal(result.code, 1);
  assert.equal(result.signal, null);
  assert.equal(result.stdout, '');
  assert.equal(result.stderr, 'realm: preflight/missing_web_secret\n'
    + 'realm: add KEYCLOAK_WEB_CLIENT_SECRET explicitly to the selected private file; see infra/keycloak/README.md\n');
  assert.ok(!result.stderr.includes(canary));
  assert.equal(await readFile(file, 'utf8'), source);
});

test('pre-aborted setup does not start target inspection or expose cancellation details', async () => {
  await assert.rejects(reconcileDevelopmentRealm({ signal: AbortSignal.abort(new Error(canary)),
    selected: { mode: 'foreign', cwd: repositoryRoot, env: { PATH: '' } },
  }), error => {
    assert.equal(realmFailure(error), 'realm: preflight/cancelled');
    assert.ok(!error.stack.includes(canary));
    return true;
  });
});
