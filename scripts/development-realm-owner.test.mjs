import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import {
  chmod, link, lstat, mkdir, mkdtemp, open, readFile, readdir, rm, symlink, unlink, utimes, writeFile,
} from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { RealmError, realmFailure } from './development-realm-errors.mjs';
import {
  acquireRealmLock, readRealmEnvironmentFile, realmEnvironment, validateLocalDockerContext, validateRealmTarget,
} from './development-realm-owner.mjs';

const keys = ['KEYCLOAK_ADMIN', 'KEYCLOAK_ADMIN_PASSWORD', 'KEYCLOAK_WEB_CLIENT_SECRET'];
const values = {
  KEYCLOAK_ADMIN: 'synthetic-admin',
  KEYCLOAK_ADMIN_PASSWORD: 'synthetic-private-password-82b1',
  KEYCLOAK_WEB_CLIENT_SECRET: 'synthetic-private-web-secret-82b1',
};
const source = keys.map((key) => `${key}=${values[key]}`).join('\n');

function refusal(stage = 'preflight', reason = 'configuration') {
  return (error) => {
    assert.ok(error instanceof RealmError);
    assert.equal(realmFailure(error), `realm: ${stage}/${reason}`);
    for (const value of Object.values(values)) assert.ok(!error.message.includes(value));
    return true;
  };
}

function target() {
  const id = 'a'.repeat(64);
  return {
    selected: { project: 'selected-development' },
    model: {
      name: 'selected-development',
      services: {
        keycloak: { image: 'quay.io/keycloak/keycloak:26.6.2', environment: { ...values } },
      },
    },
    containers: [id],
    inspected: {
      Id: id,
      Config: {
        Image: 'quay.io/keycloak/keycloak:26.6.2',
        Labels: {
          'com.docker.compose.project': 'selected-development',
          'com.docker.compose.service': 'keycloak',
        },
        Env: keys.map((key) => `${key}=${values[key]}`),
      },
      State: { Running: true },
      NetworkSettings: { Ports: { '8080/tcp': [{ HostIp: '127.0.0.1', HostPort: '8080' }] } },
    },
    context: [{ Endpoints: { docker: { Host: 'unix:///var/run/docker.sock' } } }],
  };
}

function validate(f) {
  return validateRealmTarget(f.selected, f.model, f.containers, f.inspected, f.context, values);
}

async function directory(t) {
  const path = await mkdtemp(join(tmpdir(), 'learnstack-realm-owner-test-'));
  await chmod(path, 0o700);
  t.after(() => rm(path, { recursive: true, force: true }));
  return path;
}

test('private environment is read from a regular owner-only file without changing it', async t => {
  const root = await directory(t);
  const file = join(root, 'private.env');
  await writeFile(file, source, { mode: 0o600 });
  assert.equal(await readRealmEnvironmentFile(file), source);
  assert.equal((await lstat(file)).mode & 0o777, 0o600);
  assert.equal(await readFile(file, 'utf8'), source);
});

for (const mode of [0o644, 0o620, 0o604]) {
  test(`private environment refuses shared mode ${mode.toString(8)} without repairing it`, async t => {
    const root = await directory(t);
    const file = join(root, 'private.env');
    await writeFile(file, source, { mode });
    await chmod(file, mode);
    await assert.rejects(readRealmEnvironmentFile(file), refusal());
    assert.equal((await lstat(file)).mode & 0o777, mode);
    assert.equal(await readFile(file, 'utf8'), source);
  });
}

test('private environment refuses symlinks, multiple links, directories and oversized files', async t => {
  const root = await directory(t);
  const file = join(root, 'private.env');
  await writeFile(file, source, { mode: 0o600 });
  const alias = join(root, 'alias');
  await symlink(file, alias);
  await assert.rejects(readRealmEnvironmentFile(alias), refusal());
  await unlink(alias);
  await link(file, alias);
  await assert.rejects(readRealmEnvironmentFile(file), refusal());
  await unlink(alias);
  await assert.rejects(readRealmEnvironmentFile(root), refusal());
  await writeFile(file, 'x'.repeat(256 * 1024 + 1));
  await assert.rejects(readRealmEnvironmentFile(file), refusal());
});

test('private environment refuses a different owner before using its content', async t => {
  const root = await directory(t);
  const file = join(root, 'private.env');
  await writeFile(file, source, { mode: 0o600 });
  const uid = process.getuid();
  t.mock.method(process, 'getuid', () => uid + 1);
  await assert.rejects(readRealmEnvironmentFile(file), refusal());
});

test('effective Docker host must agree with the inspected local context before stack work', () => {
  const context = target().context;
  assert.doesNotThrow(() => validateLocalDockerContext({}, context));
  assert.doesNotThrow(() => validateLocalDockerContext({ DOCKER_HOST: 'unix:///var/run/docker.sock' }, context));
  for (const environment of [
    { DOCKER_HOST: 'tcp://remote.invalid:2375' },
    { DOCKER_HOST: 'unix:///different-engine.sock' },
    { DOCKER_TLS_VERIFY: '1' }, { DOCKER_CERT_PATH: '/remote-certs' },
  ]) assert.throws(() => validateLocalDockerContext(environment, context), refusal());
});

test('the selected environment supplies exact private values and permits matching inheritance', () => {
  assert.deepEqual(realmEnvironment(source, {}), values);
  assert.deepEqual(realmEnvironment(source, { ...values, FOREIGN_SETTING: 'preserved' }), values);
  assert.ok(Object.isFrozen(realmEnvironment(source, {})));
});

test('private dotenv values remain literal data with no shell or variable evaluation', () => {
  const literal = '$(printf should-not-execute) `printf should-not-execute` ${HOME} $HOME ; value';
  const parsed = realmEnvironment(source.replace(values.KEYCLOAK_ADMIN_PASSWORD, `"${literal}"`), {});
  assert.equal(parsed.KEYCLOAK_ADMIN_PASSWORD, literal);
});

for (const key of keys) {
  test(`${key} refuses inherited disagreement, missing values and duplicate assignments`, () => {
    assert.throws(() => realmEnvironment(source, { [key]: 'different' }), refusal());
    assert.throws(
      () => realmEnvironment(source.split('\n').filter((line) => !line.startsWith(key + '=')).join('\n'), {}),
      refusal('preflight', key === 'KEYCLOAK_WEB_CLIENT_SECRET' ? 'missing_web_secret' : 'configuration'),
    );
    assert.throws(
      () => realmEnvironment(`${source}\n${key}=duplicate`, {}),
      refusal('preflight', key === 'KEYCLOAK_WEB_CLIENT_SECRET' ? 'missing_web_secret' : 'configuration'),
    );
  });
}

test('dotenv export and whitespace aliases cannot conceal duplicate private assignments', () => {
  for (const assignment of [
    'export KEYCLOAK_ADMIN_PASSWORD=conflicting',
    ' KEYCLOAK_ADMIN_PASSWORD=conflicting',
    'KEYCLOAK_ADMIN_PASSWORD = conflicting',
    '\texport\tKEYCLOAK_ADMIN_PASSWORD = conflicting',
  ]) assert.throws(() => realmEnvironment(`${source}\n${assignment}`, {}), refusal());
  for (const assignment of [
    'export KEYCLOAK_ADMIN_PASSWORD=single',
    ' KEYCLOAK_ADMIN_PASSWORD=single',
  ]) assert.throws(
    () => realmEnvironment(source.replace(`KEYCLOAK_ADMIN_PASSWORD=${values.KEYCLOAK_ADMIN_PASSWORD}`, assignment), {}),
    refusal(),
  );
});

test('multiline, NUL and oversized environment data is refused', () => {
  for (const invalid of [
    `${source}\nOTHER=\0`,
    source.replace(values.KEYCLOAK_ADMIN_PASSWORD, '"first\nsecond"'),
    `${source}\nOTHER=${'x'.repeat(256 * 1024)}`,
  ]) assert.throws(() => realmEnvironment(invalid, {}), refusal());
});

test('the exact selected pinned running local target supplies the fixed realm identity', () => {
  assert.deepEqual(validate(target()), {
    baseUrl: 'http://localhost:8080/',
    expectedIssuer: 'http://localhost:8080/realms/learnstack',
    lockIdentity: `${'a'.repeat(64)}/realms/learnstack`,
  });
  assert.ok(Object.isFrozen(validate(target())));
});

const mismatches = [
  ['selected project', (f) => { f.selected.project = 'foreign'; }],
  ['model project', (f) => { f.model.name = 'foreign'; }],
  ['service missing', (f) => { delete f.model.services.keycloak; }],
  ['service image', (f) => { f.model.services.keycloak.image = 'quay.io/keycloak/keycloak:latest'; }],
  ['container image', (f) => { f.inspected.Config.Image = 'quay.io/keycloak/keycloak:26.6.1'; }],
  ['container missing', (f) => { f.containers = []; }],
  ['multiple containers', (f) => { f.containers.push('b'.repeat(64)); }],
  ['short container identifier', (f) => { f.containers = ['a'.repeat(12)]; }],
  ['container identifier disagreement', (f) => { f.inspected.Id = 'b'.repeat(64); }],
  ['stopped container', (f) => { f.inspected.State.Running = false; }],
  ['project label', (f) => { f.inspected.Config.Labels['com.docker.compose.project'] = 'foreign'; }],
  ['service label', (f) => { f.inspected.Config.Labels['com.docker.compose.service'] = 'foreign'; }],
  ['public binding', (f) => { f.inspected.NetworkSettings.Ports['8080/tcp'][0].HostIp = '0.0.0.0'; }],
  ['foreign address binding', (f) => { f.inspected.NetworkSettings.Ports['8080/tcp'][0].HostIp = '192.0.2.15'; }],
  ['hostname binding', (f) => { f.inspected.NetworkSettings.Ports['8080/tcp'][0].HostIp = 'localhost'; }],
  ['ambiguous IPv6 binding', (f) => { f.inspected.NetworkSettings.Ports['8080/tcp'][0].HostIp = '::'; }],
  ['foreign port', (f) => { f.inspected.NetworkSettings.Ports['8080/tcp'][0].HostPort = '8081'; }],
  ['second binding', (f) => { f.inspected.NetworkSettings.Ports['8080/tcp'].push({ HostIp: '::', HostPort: '8080' }); }],
  ['missing binding', (f) => { delete f.inspected.NetworkSettings.Ports['8080/tcp']; }],
  ['remote TCP context', (f) => { f.context[0].Endpoints.docker.Host = 'tcp://remote.invalid:2375'; }],
  ['remote SSH context', (f) => { f.context[0].Endpoints.docker.Host = 'ssh://remote.invalid'; }],
  ['empty Unix socket context', (f) => { f.context[0].Endpoints.docker.Host = 'unix://'; }],
  ['relative Unix socket context', (f) => { f.context[0].Endpoints.docker.Host = 'unix://relative/docker.sock'; }],
  ['malformed context host', (f) => { f.context[0].Endpoints.docker.Host = { host: 'unix:///tmp/socket' }; }],
  ['multiple contexts', (f) => { f.context.push(structuredClone(f.context[0])); }],
  ['missing context', (f) => { f.context = []; }],
  ['missing private environment', (f) => { delete f.inspected.Config.Env; }],
  ['malformed private environment', (f) => { f.inspected.Config.Env.push(null); }],
];
for (const [name, mutate] of mismatches) {
  test(`${name} is refused before authentication`, () => {
    const f = target();
    mutate(f);
    assert.throws(() => validate(f), refusal());
  });
}

for (const key of keys) {
  test(`${key} must agree with both selected model and actual container exactly once`, () => {
    for (const mutate of [
      (f) => { f.model.services.keycloak.environment[key] = 'different'; },
      (f) => { f.inspected.Config.Env = f.inspected.Config.Env.filter((entry) => !entry.startsWith(key + '=')); },
      (f) => { f.inspected.Config.Env.push(`${key}=different`); },
      (f) => { f.inspected.Config.Env = f.inspected.Config.Env.map((entry) => entry.startsWith(key + '=') ? `${key}=different` : entry); },
    ]) {
      const f = target();
      mutate(f);
      assert.throws(() => validate(f), refusal());
    }
  });
}

test('a second lock owner is refused and successful release permits a new owner', async (t) => {
  const parent = await directory(t);
  const path = join(parent, 'private-locks');
  const owner = await acquireRealmLock('selected-container/realms/learnstack', path);
  assert.equal((await lstat(path)).mode & 0o777, 0o700);
  assert.equal((await lstat(path)).uid, process.getuid());
  const files = await readdir(path);
  assert.equal(files.length, 1);
  assert.equal((await lstat(join(path, files[0]))).mode & 0o777, 0o600);
  await assert.rejects(acquireRealmLock('selected-container/realms/learnstack', path), refusal('lock', 'lock_busy'));
  await owner.release();
  assert.deepEqual(await readdir(path), []);
  await assert.rejects(owner.release(), refusal('cleanup', 'cleanup'));
  const next = await acquireRealmLock('selected-container/realms/learnstack', path);
  await next.release();
});

test('an arbitrarily old lock is never reclaimed from elapsed age', async (t) => {
  const path = await directory(t);
  const identity = 'selected-container/realms/learnstack';
  const name = createHash('sha256').update(identity).digest('hex') + '.lock';
  const marker = join(path, name);
  await writeFile(marker, 'foreign-owner');
  await utimes(marker, new Date(0), new Date(0));
  await assert.rejects(acquireRealmLock(identity, path), refusal('lock', 'lock_busy'));
  assert.equal(await readFile(marker, 'utf8'), 'foreign-owner');
});

test('different target identities have independent local owners', async (t) => {
  const path = await directory(t);
  const first = await acquireRealmLock('one/realms/learnstack', path);
  const second = await acquireRealmLock('two/realms/learnstack', path);
  assert.equal((await readdir(path)).length, 2);
  await first.release();
  await second.release();
});

for (const operation of ['writeFile', 'stat', 'close']) {
  test(`failed lock ${operation} closes the handle and removes the exclusive marker`, async t => {
    const path = await directory(t);
    const probe = join(path, 'handle-prototype');
    const handle = await open(probe, 'wx');
    const prototype = Object.getPrototypeOf(handle);
    const method = operation === 'close' ? 'writeFile' : operation;
    const original = prototype[method];
    await handle.close();
    await unlink(probe);
    let failedHandle;
    const failure = t.mock.method(prototype, method, async function(...args) {
      failedHandle = this;
      if (operation === 'close') {
        const close = this.close;
        this.close = async () => {
          await close.call(this);
          throw new Error('synthetic lock close failure');
        };
        return original.apply(this, args);
      }
      if (operation === 'writeFile') await original.call(this, 'partial nonce');
      throw new Error('synthetic lock initialization failure');
    });
    await assert.rejects(acquireRealmLock('selected', path), refusal('lock', 'configuration'));
    assert.equal(failedHandle.fd, -1);
    assert.deepEqual(await readdir(path), []);
    failure.mock.restore();
    const next = await acquireRealmLock('selected', path);
    await next.release();
  });
}

test('permissive, non-directory and symlink lock directories are refused', async (t) => {
  const parent = await directory(t);
  const permissive = join(parent, 'permissive');
  await mkdir(permissive, { mode: 0o755 });
  await chmod(permissive, 0o755);
  const file = join(parent, 'file');
  await writeFile(file, 'foreign');
  const linked = join(parent, 'linked');
  await symlink(parent, linked);
  for (const path of [permissive, file, linked]) {
    await assert.rejects(acquireRealmLock('selected', path), refusal('lock', 'configuration'));
  }
  assert.equal(await readFile(file, 'utf8'), 'foreign');
});

for (const replacement of ['file', 'symlink', 'contents']) {
  test(`release refuses ${replacement} replacement and preserves the foreign marker`, async (t) => {
    const path = await directory(t);
    const owner = await acquireRealmLock('selected', path);
    const marker = join(path, (await readdir(path))[0]);
    const foreign = join(path, 'foreign');
    await writeFile(foreign, 'foreign-owner');
    if (replacement === 'contents') await writeFile(marker, 'foreign-owner');
    else {
      await unlink(marker);
      if (replacement === 'file') await writeFile(marker, 'foreign-owner');
      else await symlink(foreign, marker);
    }
    await assert.rejects(owner.release(), refusal('cleanup', 'cleanup'));
    assert.equal(await readFile(marker, 'utf8'), 'foreign-owner');
    assert.equal(await readFile(foreign, 'utf8'), 'foreign-owner');
  });
}
