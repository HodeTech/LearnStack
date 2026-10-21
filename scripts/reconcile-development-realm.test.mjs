import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import { access, copyFile, mkdir, mkdtemp, readFile, rm, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { realmFailure } from './development-realm-errors.mjs';
import { reconcileDevelopmentRealm } from './reconcile-development-realm.mjs';
import { repositoryRoot } from './compose.mjs';

const canary = 'synthetic-private-cli-canary-73f2';
const entry = fileURLToPath(new URL('./reconcile-development-realm.mjs', import.meta.url));

async function cli(args, environment = {}, script = entry) {
  const child = spawn(process.execPath, [script, ...args], {
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
  test(`the closed CLI rejects ${args[0]} before selecting or authenticating a target`, async t => {
    const dir = await mkdtemp(join(tmpdir(), 'learnstack-realm-argument-'));
    t.after(() => rm(dir, { recursive: true, force: true }));
    const file = join(dir, '.env');
    await writeFile(file, `KEYCLOAK_ADMIN=synthetic-admin\nKEYCLOAK_ADMIN_PASSWORD=${canary}\n`, { mode: 0o600 });
    const environment = { LEARNSTACK_COMPOSE_ENV_FILE: file };
    // A removed argv guard reaches a DIFFERENT closed downstream refusal.
    assert.match((await cli([], environment)).stderr, /preflight\/missing_web_secret/);
    const result = await cli(args, environment);
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

for (const [name, script, args] of [
  ['reconcile', entry, []],
  ['seed validation', fileURLToPath(new URL('./seed.mjs', import.meta.url)), ['--validate-config']],
]) {
  for (const kind of ['FIFO', 'symlink', 'oversized file']) {
    test(`the actual ${name} CLI refuses a selected ${kind} before reading or starting commands`, async t => {
      const root = await mkdtemp(join(tmpdir(), 'learnstack-realm-private-entry-'));
      t.after(() => rm(root, { recursive: true, force: true }));
      const file = join(root, 'selected.env');
      if (kind === 'FIFO') execFileSync('mkfifo', [file], { stdio: 'ignore' });
      else if (kind === 'symlink') {
        const target = join(root, 'private.env');
        await writeFile(target, `KEYCLOAK_ADMIN_PASSWORD=${canary}\n`, { mode: 0o600 });
        await symlink(target, file);
      } else await writeFile(file, '#'.repeat(256 * 1024 + 1), { mode: 0o600 });
      const result = await cli(args, { LEARNSTACK_COMPOSE_ENV_FILE: file }, script);
      // A blocked synchronous read needs the helper's watchdog and produces
      // SIGKILL. Successful refusal exits naturally with the closed diagnostic.
      assert.equal(result.signal, null);
      assert.equal(result.code, 1);
      assert.equal(result.stdout, '');
      if (kind === 'oversized file') assert.equal(result.stderr, 'realm: preflight/configuration\n');
      else {
        assert.ok(result.stderr.startsWith('realm: preflight/private_file\n'));
        for (const text of ['regular', 'single-link', 'current user', 'owner-only', 'chmod 600'])
          assert.ok(result.stderr.includes(text));
      }
      assert.ok(!result.stderr.includes(canary));
      assert.ok(!result.stderr.includes(file));
    });
  }
}

async function ownedEntryFixture(t, scenario) {
  const root = await mkdtemp(join(tmpdir(), 'learnstack-real-realm-entry-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  for (const directory of ['scripts', 'bin', 'tmp', 'backend', 'infra/keycloak/realms', 'frontend/apps/web/scripts'])
    await mkdir(join(root, directory), { recursive: true });
  for (const name of ['compose', 'demo-process', 'demo-database', 'reconcile-development-realm',
    'development-realm-owner', 'development-realm-errors', 'development-realm-core', 'development-realm-plan',
    'development-realm-account', 'development-realm-mappers', 'development-realm-groups', 'development-realm-test-fixture'])
    await copyFile(new URL(`./${name}.mjs`, import.meta.url), join(root, `scripts/${name}.mjs`));
  await copyFile(new URL('./development-realm-http.mjs', import.meta.url), join(root, 'scripts/development-realm-http-real.mjs'));
  await copyFile(new URL('../frontend/apps/web/scripts/stop-test-child.mjs', import.meta.url),
    join(root, 'frontend/apps/web/scripts/stop-test-child.mjs'));
  await copyFile(new URL('../infra/keycloak/realms/learnstack.json', import.meta.url), join(root, 'infra/keycloak/realms/learnstack.json'));
  await writeFile(join(root, 'package.json'), '{"type":"module"}\n');
  const { secret } = await import('./development-realm-test-fixture.mjs');
  const values = { KEYCLOAK_ADMIN: 'synthetic-admin', KEYCLOAK_ADMIN_PASSWORD: canary, KEYCLOAK_WEB_CLIENT_SECRET: secret };
  const id = randomBytes(32).toString('hex');
  const lock = join(root, 'tmp', `learnstack-realm-${process.getuid()}`,
    createHash('sha256').update(id + '/realms/learnstack').digest('hex') + '.lock');
  const events = join(root, 'events');
  const ready = join(root, 'ready');
  await writeFile(join(root, '.env'), Object.entries(values).map(([key, value]) => `${key}=${value}`).join('\n'), { mode: 0o600 });
  // Only the external provider boundary is substituted in this private copy.
  // CLI, target validation, lock, manifest, full planner/readback and cleanup are real.
  await writeFile(join(root, 'scripts/development-realm-http.mjs'), `
import { appendFileSync, existsSync, writeFileSync } from 'node:fs';
import { provider } from './development-realm-test-fixture.mjs';
import { refuse } from './development-realm-errors.mjs';
export { realmJson } from './development-realm-http-real.mjs';
const fixture = provider();
if (${JSON.stringify(scenario)} === 'failure') fixture.state.realm.accessTokenLifespan = 1800;
export async function realmRequest(url, options) {
  if (!existsSync(${JSON.stringify(lock)})) throw new Error('No lock before provider use');
  appendFileSync(${JSON.stringify(events)}, JSON.stringify({ provider: true, method: options.method ?? 'GET', stage: options.stage }) + '\\n');
  if (${JSON.stringify(scenario)} === 'cancel') {
    writeFileSync(${JSON.stringify(ready)}, 'ready');
    if (!options.signal.aborted) await new Promise(resolve => options.signal.addEventListener('abort', resolve, { once: true }));
    refuse(options.stage, 'cancelled');
  }
  if (${JSON.stringify(scenario)} === 'failure' && options.method === 'PUT') refuse('apply', 'transport');
  let body;
  if (url.pathname.includes('/.well-known/')) body = { issuer: 'http://localhost:8080/realms/learnstack' };
  else if (url.pathname.endsWith('/token')) body = { access_token: 'synthetic-admin-token', expires_in: 300, refresh_token: 'synthetic-refresh' };
  else if (url.pathname === '/admin/serverinfo') body = { systemInfo: { version: '26.6.2' } };
  else {
    const path = url.pathname.slice('/admin/realms/learnstack/'.length) + url.search;
    if (options.method === 'PUT') { await fixture.admin.put(path, options.json); return { status: 204, bytes: Buffer.alloc(0) }; }
    if (options.method === 'POST') { const result = await fixture.admin.post(path, options.json); return { status: result.status, bytes: Buffer.from(JSON.stringify(result.body ?? {})) }; }
    body = await fixture.admin.get(path);
  }
  return { status: 200, bytes: Buffer.from(JSON.stringify(body)) };
}
`);
  for (const command of ['docker', 'dotnet']) {
    await writeFile(join(root, 'bin', command), `#!${process.execPath}
import { appendFileSync } from 'node:fs';
import { manifest } from '../scripts/development-realm-test-fixture.mjs';
const args = process.argv.slice(2);
appendFileSync(${JSON.stringify(events)}, JSON.stringify({ command: ${JSON.stringify(command)}, args,
  privateEnvironment: Object.keys(process.env).some(key => /keycloak|connectionstrings|password|_pw$/i.test(key)) }) + '\\n');
const values = ${JSON.stringify(values)};
let result;
if (${JSON.stringify(command)} === 'dotnet') {
  if (args.length !== 2 || args[1] !== '--identity-manifest') process.exit(19);
  result = manifest;
} else if (args[0] === 'context') result = [{ Endpoints: { docker: { Host: 'unix:///var/run/docker.sock' } } }];
else if (args.includes('config')) result = { name: 'learnstack-dev', services: { keycloak: { image: 'quay.io/keycloak/keycloak:26.6.2', environment: values } } };
else if (args.includes('ps')) { process.stdout.write(${JSON.stringify(id)}); process.exit(0); }
else if (args[0] === 'inspect') result = [{ Id: ${JSON.stringify(id)}, State: { Running: true },
 Config: { Image: 'quay.io/keycloak/keycloak:26.6.2', Labels: { 'com.docker.compose.project': 'learnstack-dev', 'com.docker.compose.service': 'keycloak' }, Env: Object.entries(values).map(([key, value]) => key + '=' + value) },
 NetworkSettings: { Ports: { '8080/tcp': [{ HostIp: '127.0.0.1', HostPort: '8080' }] } } }];
else process.exit(19);
process.stdout.write(JSON.stringify(result));
`, { mode: 0o700 });
  }
  return { root, lock, events, ready, values };
}

for (const scenario of ['success', 'failure', 'cancel']) {
  test(`the actual CLI ${scenario} path owns target, manifest, provider and lock cleanup`, async t => {
    const f = await ownedEntryFixture(t, scenario);
    const child = spawn(process.execPath, [join(f.root, 'scripts/reconcile-development-realm.mjs')], {
      cwd: f.root, env: { PATH: join(f.root, 'bin'), HOME: f.root, TMPDIR: join(f.root, 'tmp'),
        ...f.values, ConnectionStrings__Default: canary, POSTGRES_PASSWORD: canary }, stdio: ['ignore', 'pipe', 'pipe'],
    });
    const output = [], errors = [];
    child.stdout.on('data', value => output.push(value));
    child.stderr.on('data', value => errors.push(value));
    const status = new Promise((resolve, reject) => {
      child.once('error', reject);
      child.once('close', (code, signal) => resolve({ code, signal }));
    });
    const timer = setTimeout(() => child.kill('SIGKILL'), 10000);
    try {
      if (scenario === 'cancel') {
        const deadline = Date.now() + 5000;
        while (!await access(f.ready).then(() => true, () => false)) {
          if (Date.now() >= deadline) throw new Error('Owned provider did not start');
          await new Promise(resolve => setTimeout(resolve, 10));
        }
        child.kill('SIGTERM');
      }
      assert.deepEqual(await status, { code: scenario === 'success' ? 0 : 1, signal: null },
        Buffer.concat(errors).toString() + await readFile(f.events, 'utf8').catch(() => 'no commands'));
    } finally {
      clearTimeout(timer);
      if (child.exitCode === null && child.signalCode === null) { child.kill('SIGKILL'); await status; }
    }
    const stdout = Buffer.concat(output).toString(), stderr = Buffer.concat(errors).toString();
    assert.equal(stdout, scenario === 'success' ? 'realm: completed (0 writes)\n' : '');
    assert.equal(stderr, scenario === 'success' ? '' : `realm: ${scenario === 'failure' ? 'apply/transport' : 'discovery/cancelled'}\n`);
    for (const value of [canary, 'private-test-source', 'synthetic-admin-token', 'synthetic-refresh'])
      assert.ok(!(stdout + stderr).includes(value));
    assert.equal(await access(f.lock).then(() => true, () => false), false);
    const events = (await readFile(f.events, 'utf8')).trim().split('\n').map(value => JSON.parse(value));
    assert.equal(events.filter(value => value.command === 'dotnet').length, 1);
    assert.ok(events.some(value => value.provider));
    assert.ok(events.filter(value => value.command).every(value => !value.privateEnvironment));
    for (const privateValue of [canary, 'private-test-source', 'synthetic-admin-token', 'synthetic-refresh'])
      assert.ok(events.filter(value => value.command).every(value => !JSON.stringify(value.args).includes(privateValue)));
  });
}

test('a symlink CLI invocation executes the same closed argument guard', async t => {
  const root = await mkdtemp(join(tmpdir(), 'learnstack-realm-symlink-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const alias = join(root, 'realm.mjs');
  await symlink(entry, alias);
  const child = spawn(process.execPath, [alias, '--reset'], { env: { PATH: '' }, stdio: ['ignore', 'pipe', 'pipe'] });
  let output = '', errors = '';
  child.stdout.on('data', value => { output += value; });
  child.stderr.on('data', value => { errors += value; });
  const code = await new Promise(resolve => child.once('close', resolve));
  assert.equal(code, 1);
  assert.equal(output, '');
  assert.equal(errors, 'realm: preflight/configuration\n');
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
