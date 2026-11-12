import test, { before, after } from 'node:test';
import assert from 'node:assert/strict';
import { execFile, execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { chmod, copyFile, lstat, mkdtemp, mkdir, readFile, readdir, realpath, rename, rm, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { promisify } from 'node:util';
import { developmentBackchannelConfiguration, developmentBackchannelUrl, initializeKeycloakTrust,
  validateKeycloakBackchannelTrust } from './development-backchannel.mjs';
import { setupDevelopmentBackchannelTls } from './setup-development-backchannel.mjs';

let root;
let source;
const execute = promisify(execFile);
before(async () => {
  root = await mkdtemp(join(tmpdir(), 'learnstack-backchannel-'));
  await setupDevelopmentBackchannelTls(root);
  execFileSync('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '2',
    '-subj', '/CN=localhost', '-addext', 'subjectAltName=DNS:localhost',
    '-addext', 'basicConstraints=critical,CA:TRUE', '-keyout', join(root, 'native-key.pem'),
    '-out', join(root, 'native.pem')], { stdio: 'ignore', timeout: 15000 });
  await chmod(join(root, 'native-key.pem'), 0o600);
  source = `LEARNSTACK_BFF_ENABLED=true
LEARNSTACK_BFF_BACKCHANNEL_ENABLED=true
LEARNSTACK_BFF_BACKCHANNEL_URL=https://host.docker.internal:3443/api/auth/backchannel/keycloak
LEARNSTACK_BFF_BACKCHANNEL_BIND_ADDRESS=0.0.0.0
LEARNSTACK_BFF_BACKCHANNEL_TLS_CERT=.data/tls/backchannel/leaf.pem
LEARNSTACK_BFF_BACKCHANNEL_TLS_KEY=.data/tls/backchannel/leaf-key.pem
LEARNSTACK_BFF_BACKCHANNEL_CA=.data/tls/backchannel/ca.pem
LEARNSTACK_BFF_BACKCHANNEL_NATIVE_CA=native.pem
LEARNSTACK_PUBLIC_TLS_CERT=native.pem
LEARNSTACK_PUBLIC_TLS_KEY=native-key.pem
`;
});
after(async () => { await rm(root, { recursive: true, force: true }); });
const config = (text = source, extra = {}) => developmentBackchannelConfiguration({ source: text, root, ambient: {}, ...extra });
const rejected = operation => assert.rejects(operation, error => error.reason === 'backchannel_configuration');

test('explicit operator setup creates bounded owned material, deletes root private key, never overwrites', async () => {
  const directory = join(root, '.data/tls/backchannel');
  assert.deepEqual((await readdir(directory)).sort(), ['ca.pem', 'leaf-key.pem', 'leaf.pem']);
  for (const name of await readdir(directory)) assert.equal((await lstat(join(directory, name))).mode & 0o777, 0o600);
  const before = await readFile(join(directory, 'leaf-key.pem'));
  await rejected(setupDevelopmentBackchannelTls(root));
  assert.deepEqual(await readFile(join(directory, 'leaf-key.pem')), before);
  assert.equal((await config()).enabled, true);
});

test('normal Compose up transitions disabled empty trust to the verified root identity without caller overrides', async () => {
  const empty = await realpath(await mkdtemp(join(tmpdir(), 'learnstack-backchannel-disabled-')));
  try {
    const configuration = await developmentBackchannelConfiguration({ source: '', ambient: {}, root: empty });
    assert.deepEqual(configuration, { enabled: false });
    assert.equal(await initializeKeycloakTrust({ root: empty, configuration }), 'empty');
    assert.deepEqual(await readdir(join(empty, '.data/keycloak-trust')), []);
    for (const directory of ['scripts', 'bin', 'infra/compose', 'frontend/apps/web/scripts']) await mkdir(join(empty, directory), { recursive: true });
    for (const name of ['compose', 'development-backchannel', 'development-realm-errors'])
      await copyFile(new URL(`./${name}.mjs`, import.meta.url), join(empty, `scripts/${name}.mjs`));
    await copyFile(new URL('../frontend/apps/web/scripts/tls-server-qualification.mjs', import.meta.url),
      join(empty, 'frontend/apps/web/scripts/tls-server-qualification.mjs'));
    await copyFile(new URL('../infra/compose/dev.yml', import.meta.url), join(empty, 'infra/compose/dev.yml'));
    const recorded = join(empty, 'docker-calls.jsonl');
    await writeFile(join(empty, 'bin/docker'), `#!${process.execPath}
import { appendFileSync } from 'node:fs';
appendFileSync(${JSON.stringify(recorded)}, JSON.stringify({
  digest: process.env.LEARNSTACK_KEYCLOAK_TRUST_SHA256,
  args: process.argv.slice(2),
  // macOS/libuv may inject this encoding marker into a spawned process.
  environmentKeys: Object.keys(process.env).filter(key => key !== '__CF_USER_TEXT_ENCODING').sort(),
}) + '\\n');
`, { mode: 0o700 });
    // The owned CLI receives no workstation configuration. Its selected source
    // is synthetic, and only nonsecret identity/argument evidence is recorded.
    const selectedSource = join(empty, '.env');
    const key = 'synthetic-keycloak-web-secret';
    await writeFile(selectedSource, `KEYCLOAK_WEB_CLIENT_SECRET=${key}\nLEARNSTACK_BFF_BACKCHANNEL_ENABLED=false\n`, { mode: 0o600 });
    const up = () => execFileSync(process.execPath, [join(empty, 'scripts/compose.mjs'), 'selected', 'up', '-d'], {
      cwd: empty, encoding: 'utf8', timeout: 10000, stdio: ['ignore', 'pipe', 'pipe'],
      env: { PATH: join(empty, 'bin'), LEARNSTACK_KEYCLOAK_TRUST_SHA256: 'forged-caller-digest',
        UNRELATED_CALLER_SECRET: 'synthetic-unrelated-secret', ConnectionStrings__Default: 'synthetic-database-secret' },
    });
    assert.equal(up(), '');
    assert.deepEqual(await readdir(join(empty, '.data/keycloak-trust')), []);
    await mkdir(join(empty, '.data/tls/backchannel'), { recursive: true, mode: 0o700 });
    for (const name of ['ca.pem', 'leaf.pem', 'leaf-key.pem']) {
      const target = join(empty, '.data/tls/backchannel', name);
      await copyFile(join(root, '.data/tls/backchannel', name), target);
      await chmod(target, 0o600);
    }
    await copyFile(join(root, 'native.pem'), join(empty, 'native.pem'));
    await copyFile(join(root, 'native-key.pem'), join(empty, 'native-key.pem'));
    await chmod(join(empty, 'native-key.pem'), 0o600);
    await writeFile(selectedSource, `KEYCLOAK_WEB_CLIENT_SECRET=${key}\n${source}`, { mode: 0o600 });
    assert.equal(up(), '');
    const publicRoot = await readFile(join(empty, '.data/tls/backchannel/ca.pem'));
    const actualDigest = createHash('sha256').update(publicRoot).digest('hex');
    const calls = (await readFile(recorded, 'utf8')).trim().split('\n').map(line => JSON.parse(line));
    assert.deepEqual(calls, ['empty', actualDigest].map(digest => ({ digest,
      args: ['compose', '--project-name', 'learnstack-dev', '--env-file', selectedSource,
        '-f', join(empty, 'infra/compose/dev.yml'), 'up', '-d'],
      environmentKeys: ['LEARNSTACK_KEYCLOAK_TRUST_SHA256', 'PATH'],
    })));
    const mountedRoot = join(empty, '.data/keycloak-trust/callback-root.pem');
    assert.deepEqual(await readFile(mountedRoot), publicRoot);
    const enabled = await developmentBackchannelConfiguration({ source, root: empty, ambient: {} });
    const inspected = { Mounts: [{ Destination: '/opt/keycloak/conf/truststores', Type: 'bind',
      Source: join(empty, '.data/keycloak-trust'), RW: false }],
      Config: { Env: ['KC_TRUSTSTORE_PATHS=/opt/keycloak/conf/truststores'],
        Labels: { 'learnstack.backchannel-trust-sha256': calls[0].digest } },
      HostConfig: { ExtraHosts: ['host.docker.internal:host-gateway'] } };
    await rejected(validateKeycloakBackchannelTrust({ root: empty, configuration: enabled, inspected }));
    inspected.Config.Labels['learnstack.backchannel-trust-sha256'] = calls[1].digest;
    await validateKeycloakBackchannelTrust({ root: empty, configuration: enabled, inspected });
  } finally { await rm(empty, { recursive: true, force: true }); }
});

test('shared preflight rejects missing, malformed, inconsistent and ambiguous settings before setup', async () => {
  const modifications = [
    value => value.replace('LEARNSTACK_BFF_ENABLED=true', 'LEARNSTACK_BFF_ENABLED=false'),
    value => value.replace('BACKCHANNEL_ENABLED=true', 'BACKCHANNEL_ENABLED=TRUE'),
    value => value.replace('LEARNSTACK_BFF_BACKCHANNEL_CA=.data/tls/backchannel/ca.pem\n', ''),
    value => value.replace('BIND_ADDRESS=0.0.0.0', 'BIND_ADDRESS=::'),
    value => value.replace('https://host.docker.internal:3443', 'http://host.docker.internal:3443'),
    value => value.replace('/api/auth/backchannel/keycloak', '/other'),
    value => value.replace('/api/auth/backchannel/keycloak', '/api/auth/backchannel/keycloak?'),
    value => value.replace(':3443/', ':0/'),
    value => value.replace(':3443/', ':65536/'),
    value => value.replace('TLS_CERT=.data/tls/backchannel/leaf.pem', 'TLS_CERT=missing.pem'),
    value => value + 'LEARNSTACK_BFF_BACKCHANNEL_ENABLED=true\n',
    value => value.replace('LEARNSTACK_BFF_BACKCHANNEL_CA=', 'export LEARNSTACK_BFF_BACKCHANNEL_CA='),
  ];
  for (const modify of modifications) await rejected(config(modify(source)));
  for (const delimiter of ['?', '#']) assert.throws(() =>
    developmentBackchannelUrl(`https://host.docker.internal:3443/api/auth/backchannel/keycloak${delimiter}`),
  error => error.reason === 'backchannel_configuration');
  await rejected(config(source, { ambient: { LEARNSTACK_BFF_BACKCHANNEL_BIND_ADDRESS: '127.0.0.1' } }));
  await rejected(config(source, { webSource: 'LEARNSTACK_BFF_BACKCHANNEL_URL=https://foreign.invalid/api/auth/backchannel/keycloak\n' }));
  await rejected(config('', { ambient: { LEARNSTACK_BFF_BACKCHANNEL_ENABLED: 'true' } }));
  assert.equal(developmentBackchannelUrl('https://host.docker.internal/api/auth/backchannel/keycloak').port, '');
  await config(source, { webSource: 'LEARNSTACK_BFF_BACKCHANNEL_ENABLED=true\n' });
});

test('web source permissions follow combined enablement without requiring a private disabled projection', async () => {
  const directory = join(root, 'frontend/apps/web');
  const path = join(directory, '.env.local');
  await mkdir(directory, { recursive: true });
  try {
    await writeFile(path, '');
    await chmod(path, 0o600);
    assert.deepEqual(await config(''), { enabled: false });
    assert.equal((await config()).enabled, true);
    await writeFile(path, 'UNRELATED=synthetic\n');
    await chmod(path, 0o664);
    assert.deepEqual(await config(''), { enabled: false });
    await rejected(config());
    await chmod(path, 0o644);
    assert.deepEqual(await config(''), { enabled: false });
    await rejected(config());
    const withoutEnablement = source.replace('LEARNSTACK_BFF_BACKCHANNEL_ENABLED=true\n', '');
    await rejected(config(withoutEnablement, { ambient: { LEARNSTACK_BFF_BACKCHANNEL_ENABLED: 'true' } }));
    await writeFile(path, 'LEARNSTACK_BFF_BACKCHANNEL_ENABLED=true\n');
    await rejected(config(withoutEnablement));
    await chmod(path, 0o600);
    assert.equal((await config(withoutEnablement)).enabled, true);
    await writeFile(path, 'LEARNSTACK_BFF_BACKCHANNEL_ENABLED=false\n');
    await rejected(config());
    assert.deepEqual(await config(''), { enabled: false });
  } finally { await rm(path); }
});

test('setup failure and handled interruption retire signing work without publishing partial output', async () => {
  const isolated = await mkdtemp(join(tmpdir(), 'learnstack-backchannel-interruption-'));
  try {
    const bin = join(isolated, 'bin');
    await mkdir(bin);
    const openssl = join(bin, 'openssl');
    const run = () => execute(process.execPath, ['--input-type=module', '-e',
      `import {setupDevelopmentBackchannelTls} from ${JSON.stringify(new URL('./setup-development-backchannel.mjs', import.meta.url).href)};
       try { await setupDevelopmentBackchannelTls(${JSON.stringify(isolated)}); } catch { process.exitCode = 1; }`],
    { env: { PATH: bin }, timeout: 10000, maxBuffer: 4096 });
    for (const signal of [undefined, 'SIGINT', 'SIGTERM']) {
      const retirement = join(bin, 'retired');
      await writeFile(openssl, `#!${process.execPath}\nrequire('node:fs').writeFileSync('ca-key.pem', 'synthetic-private-key');
        ${signal ? `process.on('SIGTERM', () => setTimeout(() => {
          require('node:fs').writeFileSync(${JSON.stringify(retirement)}, String(require('node:fs').existsSync(process.cwd())));
          process.exit(0);
        }, 40)); setTimeout(() => process.exit(2), 5000); process.kill(process.ppid, ${JSON.stringify(signal)});` : 'process.exitCode = 1;'}\n`, { mode: 0o700 });
      await assert.rejects(run(), error => error.code === 1);
      if (signal) {
        assert.equal(await readFile(retirement, 'utf8'), 'true', 'signing child closes before its staging directory is removed');
        await rm(retirement);
      }
      assert.deepEqual(await readdir(join(isolated, '.data/tls')), []);
    }
    await setupDevelopmentBackchannelTls(isolated);
    assert.deepEqual((await readdir(join(isolated, '.data/tls/backchannel'))).sort(), ['ca.pem', 'leaf-key.pem', 'leaf.pem']);
  } finally { await rm(isolated, { recursive: true, force: true }); }
});

test('retained partial trust refuses without deleting operator material and initial publication is complete', async () => {
  const isolated = await mkdtemp(join(tmpdir(), 'learnstack-backchannel-publication-'));
  try {
    const directory = join(isolated, '.data/keycloak-trust');
    await mkdir(directory, { recursive: true });
    await chmod(directory, 0o755);
    const target = join(directory, 'callback-root.pem');
    await writeFile(target, '', { mode: 0o644 });
    await rejected(initializeKeycloakTrust({ root: isolated, configuration: { enabled: false } }));
    assert.equal((await lstat(target)).size, 0);
    // Explicit test-owner removal models the documented operator inspection;
    // initialization never removes an existing partial file on its own.
    await rm(target);
    const configuration = await config();
    const attempts = await Promise.allSettled([0, 1].map(() => initializeKeycloakTrust({ root: isolated, configuration })));
    assert.ok(attempts.some(value => value.status === 'fulfilled'));
    assert.deepEqual(await readFile(target), await readFile(configuration.ca));
    assert.equal((await lstat(target)).nlink, 1);
    assert.deepEqual(await readdir(join(isolated, '.data')), ['keycloak-trust']);
  } finally { await rm(isolated, { recursive: true, force: true }); }
});

test('certificate trust, native trust, key pair and private permissions fail closed', async () => {
  for (const modify of [
    value => value.replace('BACKCHANNEL_CA=.data/tls/backchannel/ca.pem', 'BACKCHANNEL_CA=native.pem'),
    value => value.replace('NATIVE_CA=native.pem', 'NATIVE_CA=.data/tls/backchannel/ca.pem'),
    value => value.replace('TLS_KEY=.data/tls/backchannel/leaf-key.pem', 'TLS_KEY=native-key.pem'),
    value => value.replace('PUBLIC_TLS_KEY=native-key.pem', 'PUBLIC_TLS_KEY=.data/tls/backchannel/leaf-key.pem'),
    value => value.replace('https://host.docker.internal:3443', 'https://foreign.invalid:3443'),
  ]) await rejected(config(modify(source)));
  const key = join(root, '.data/tls/backchannel/leaf-key.pem');
  await chmod(key, 0o644);
  await rejected(config());
  await chmod(key, 0o600);
  const alias = join(root, 'alias.pem');
  await symlink(join(root, 'native.pem'), alias);
  await rejected(config(source.replace('NATIVE_CA=native.pem', 'NATIVE_CA=alias.pem')));
  await writeFile(join(root, 'private-root.pem'), await readFile(key), { mode: 0o600 });
  await rejected(config(source.replace('NATIVE_CA=native.pem', 'NATIVE_CA=private-root.pem')));
});

test('both callback and native preflight reject a validly signed client-authentication-only server leaf', async () => {
  const directory = await mkdtemp(join(root, 'purpose-'));
  try {
    const openssl = args => execFileSync('openssl', args, { cwd: directory, stdio: 'ignore', timeout: 15000 });
    openssl(['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-sha256', '-days', '2',
      '-subj', '/CN=purpose-test-root', '-keyout', 'ca-key.pem', '-out', 'ca.pem',
      '-addext', 'basicConstraints=critical,CA:TRUE', '-addext', 'keyUsage=critical,keyCertSign,cRLSign']);
    openssl(['req', '-newkey', 'rsa:2048', '-nodes', '-sha256', '-subj', '/CN=localhost', '-keyout', 'leaf-key.pem', '-out', 'leaf.csr']);
    await writeFile(join(directory, 'extensions.cnf'), 'subjectAltName=DNS:localhost,DNS:host.docker.internal\nbasicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=clientAuth\n');
    openssl(['x509', '-req', '-in', 'leaf.csr', '-CA', 'ca.pem', '-CAkey', 'ca-key.pem', '-CAcreateserial', '-days', '2', '-sha256', '-extfile', 'extensions.cnf', '-out', 'leaf.pem']);
    await chmod(join(directory, 'leaf-key.pem'), 0o600);
    // Signature, validity and both hostnames are correct; the real server
    // purpose check must independently reject either use of this leaf.
    await rejected(config(source
      .replace('BACKCHANNEL_TLS_CERT=.data/tls/backchannel/leaf.pem', `BACKCHANNEL_TLS_CERT=${join(directory, 'leaf.pem')}`)
      .replace('BACKCHANNEL_TLS_KEY=.data/tls/backchannel/leaf-key.pem', `BACKCHANNEL_TLS_KEY=${join(directory, 'leaf-key.pem')}`)
      .replace('BACKCHANNEL_CA=.data/tls/backchannel/ca.pem', `BACKCHANNEL_CA=${join(directory, 'ca.pem')}`)));
    await rejected(config(source
      .replace('PUBLIC_TLS_CERT=native.pem', `PUBLIC_TLS_CERT=${join(directory, 'leaf.pem')}`)
      .replace('PUBLIC_TLS_KEY=native-key.pem', `PUBLIC_TLS_KEY=${join(directory, 'leaf-key.pem')}`)
      .replace('NATIVE_CA=native.pem', `NATIVE_CA=${join(directory, 'ca.pem')}`)));
    assert.equal((await config()).enabled, true);
  } finally { await rm(directory, { recursive: true, force: true }); }
});

test('explicit owned renewal preserves retired material and requires the new provider startup trust identity', async () => {
  const isolated = await realpath(await mkdtemp(join(tmpdir(), 'learnstack-backchannel-renewal-')));
  try {
    await setupDevelopmentBackchannelTls(isolated);
    for (const name of ['native.pem', 'native-key.pem']) await copyFile(join(root, name), join(isolated, name));
    await chmod(join(isolated, 'native-key.pem'), 0o600);
    const initial = await config(source, { root: isolated });
    const oldDigest = await initializeKeycloakTrust({ root: isolated, configuration: initial });
    const tlsDirectory = join(isolated, '.data/tls/backchannel');
    const trustDirectory = join(isolated, '.data/keycloak-trust');
    const target = join(trustDirectory, 'callback-root.pem');
    const names = ['ca.pem', 'leaf.pem', 'leaf-key.pem'];
    const retired = new Map(await Promise.all(names.map(async name => [name, await readFile(join(tlsDirectory, name))])));
    const retainedRoot = await readFile(target);
    const retainedState = await lstat(target);
    assert.deepEqual(retainedRoot, retired.get('ca.pem'));

    // Another fully qualified root is still not permission to replace trust.
    const changed = await config();
    assert.notDeepEqual(await readFile(changed.ca), retainedRoot);
    await rejected(initializeKeycloakTrust({ root: isolated, configuration: changed }));
    assert.deepEqual(await readFile(target), retainedRoot);
    assert.equal((await lstat(target)).ino, retainedState.ino);

    // Only the fixture operator moves the exact identified paths to a fresh
    // private backup; setup and trust initialization never do this themselves.
    const backup = join(isolated, '.data/retired-callback');
    await mkdir(backup, { mode: 0o700 });
    await rename(tlsDirectory, join(backup, 'tls'));
    await rename(target, join(backup, 'callback-root.pem'));
    await setupDevelopmentBackchannelTls(isolated);
    const renewed = await config(source, { root: isolated });
    for (const name of names) assert.notDeepEqual(await readFile(join(tlsDirectory, name)), retired.get(name));
    const newDigest = await initializeKeycloakTrust({ root: isolated, configuration: renewed });
    assert.notEqual(newDigest, oldDigest);
    const renewedRoot = await readFile(renewed.ca);
    assert.deepEqual(await readFile(target), renewedRoot);
    assert.equal(newDigest, createHash('sha256').update(renewedRoot).digest('hex'));
    const inspected = {
      Mounts: [{ Destination: '/opt/keycloak/conf/truststores', Type: 'bind', Source: trustDirectory, RW: false }],
      Config: { Env: ['KC_TRUSTSTORE_PATHS=/opt/keycloak/conf/truststores'], Labels: { 'learnstack.backchannel-trust-sha256': oldDigest } },
      HostConfig: { ExtraHosts: ['host.docker.internal:host-gateway'] },
    };
    await rejected(validateKeycloakBackchannelTrust({ root: isolated, configuration: renewed, inspected }));
    inspected.Config.Labels['learnstack.backchannel-trust-sha256'] = newDigest;
    await validateKeycloakBackchannelTrust({ root: isolated, configuration: renewed, inspected });
    for (const name of names) assert.deepEqual(await readFile(join(backup, 'tls', name)), retired.get(name));
    assert.deepEqual(await readFile(join(backup, 'callback-root.pem')), retainedRoot);
  } finally { await rm(isolated, { recursive: true, force: true }); }
});

test('public provider trust route is exact, readonly, repeatable and refuses replacement', async () => {
  const configuration = await config();
  const trustDigest = await initializeKeycloakTrust({ root, configuration });
  assert.match(trustDigest, /^[0-9a-f]{64}$/);
  const directory = join(root, '.data/keycloak-trust');
  const path = join(directory, 'callback-root.pem');
  const bytes = await readFile(path);
  assert.deepEqual(bytes, await readFile(configuration.ca));
  assert.equal((await lstat(directory)).mode & 0o777, 0o755);
  assert.equal((await lstat(path)).mode & 0o777, 0o644);
  const state = await lstat(path);
  await initializeKeycloakTrust({ root, configuration });
  assert.equal((await lstat(path)).ino, state.ino);
  const inspected = { Mounts: [{ Destination: '/opt/keycloak/conf/truststores', Type: 'bind', Source: directory, RW: false }],
    Config: { Env: ['KC_TRUSTSTORE_PATHS=/opt/keycloak/conf/truststores'], Labels: { 'learnstack.backchannel-trust-sha256': trustDigest } },
    HostConfig: { ExtraHosts: ['host.docker.internal:host-gateway'] } };
  await validateKeycloakBackchannelTrust({ root, configuration, inspected });
  for (const modify of [
    value => { value.Mounts[0].RW = true; },
    value => { value.Mounts[0].Source = '/foreign'; },
    value => { value.Config.Env = []; },
    value => { value.HostConfig.ExtraHosts = []; },
    value => { value.Config.Labels['learnstack.backchannel-trust-sha256'] = 'empty'; },
  ]) {
    const dirty = structuredClone(inspected); modify(dirty);
    await rejected(validateKeycloakBackchannelTrust({ root, configuration, inspected: dirty }));
  }
  // A provider started while disabled cannot claim a root installed later.
  // Only a newly configured provider carrying that root identity qualifies.
  assert.equal(await initializeKeycloakTrust({ root, configuration: {enabled: false} }), trustDigest);
  await rejected(initializeKeycloakTrust({ root, configuration: { ...configuration, ca: join(root, 'native.pem') } }));
  assert.deepEqual(await readFile(path), bytes);
  await mkdir(join(directory, 'unexpected'));
  await rejected(initializeKeycloakTrust({ root, configuration }));
});
