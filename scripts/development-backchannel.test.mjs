import test, { before, after } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { chmod, lstat, mkdtemp, mkdir, readFile, readdir, rm, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { developmentBackchannelConfiguration, developmentBackchannelUrl, initializeKeycloakTrust,
  validateKeycloakBackchannelTrust } from './development-backchannel.mjs';
import { setupDevelopmentBackchannelTls } from './setup-development-backchannel.mjs';

let root;
let source;
before(async () => {
  root = await mkdtemp(join(tmpdir(), 'learnstack-backchannel-'));
  await setupDevelopmentBackchannelTls(root);
  execFileSync('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '2',
    '-subj', '/CN=localhost', '-addext', 'subjectAltName=DNS:localhost',
    '-addext', 'basicConstraints=critical,CA:TRUE', '-keyout', join(root, 'native-key.pem'),
    '-out', join(root, 'native.pem')], { stdio: 'ignore', timeout: 15000 });
  source = `LEARNSTACK_BFF_ENABLED=true
LEARNSTACK_BFF_BACKCHANNEL_ENABLED=true
LEARNSTACK_BFF_BACKCHANNEL_URL=https://host.docker.internal:3443/api/auth/backchannel/keycloak
LEARNSTACK_BFF_BACKCHANNEL_BIND_ADDRESS=0.0.0.0
LEARNSTACK_BFF_BACKCHANNEL_TLS_CERT=.data/tls/backchannel/leaf.pem
LEARNSTACK_BFF_BACKCHANNEL_TLS_KEY=.data/tls/backchannel/leaf-key.pem
LEARNSTACK_BFF_BACKCHANNEL_CA=.data/tls/backchannel/ca.pem
LEARNSTACK_BFF_BACKCHANNEL_NATIVE_CA=native.pem
LEARNSTACK_PUBLIC_TLS_CERT=native.pem
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

test('disabled defaults require no TLS files or trust file mount', async () => {
  const empty = await mkdtemp(join(tmpdir(), 'learnstack-backchannel-disabled-'));
  try {
    const configuration = await developmentBackchannelConfiguration({ source: '', ambient: {}, root: empty });
    assert.deepEqual(configuration, { enabled: false });
    await initializeKeycloakTrust({ root: empty, configuration });
    assert.deepEqual(await readdir(join(empty, '.data/keycloak-trust')), []);
    await initializeKeycloakTrust({ root: empty, configuration });
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
    value => value.replace(':3443/', ':0/'),
    value => value.replace(':3443/', ':65536/'),
    value => value.replace('TLS_CERT=.data/tls/backchannel/leaf.pem', 'TLS_CERT=missing.pem'),
    value => value + 'LEARNSTACK_BFF_BACKCHANNEL_ENABLED=true\n',
    value => value.replace('LEARNSTACK_BFF_BACKCHANNEL_CA=', 'export LEARNSTACK_BFF_BACKCHANNEL_CA='),
  ];
  for (const modify of modifications) await rejected(config(modify(source)));
  await rejected(config(source, { ambient: { LEARNSTACK_BFF_BACKCHANNEL_BIND_ADDRESS: '127.0.0.1' } }));
  await rejected(config(source, { webSource: 'LEARNSTACK_BFF_BACKCHANNEL_URL=https://foreign.invalid/api/auth/backchannel/keycloak\n' }));
  await rejected(config('', { ambient: { LEARNSTACK_BFF_BACKCHANNEL_ENABLED: 'true' } }));
  assert.equal(developmentBackchannelUrl('https://host.docker.internal/api/auth/backchannel/keycloak').port, '');
  await config(source, { webSource: 'LEARNSTACK_BFF_BACKCHANNEL_ENABLED=true\n' });
});

test('certificate trust, native trust, key pair and private permissions fail closed', async () => {
  for (const modify of [
    value => value.replace('BACKCHANNEL_CA=.data/tls/backchannel/ca.pem', 'BACKCHANNEL_CA=native.pem'),
    value => value.replace('NATIVE_CA=native.pem', 'NATIVE_CA=.data/tls/backchannel/ca.pem'),
    value => value.replace('TLS_KEY=.data/tls/backchannel/leaf-key.pem', 'TLS_KEY=native-key.pem'),
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

test('public provider trust route is exact, readonly, repeatable and refuses replacement', async () => {
  const configuration = await config();
  await initializeKeycloakTrust({ root, configuration });
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
    Config: { Env: ['KC_TRUSTSTORE_PATHS=/opt/keycloak/conf/truststores'] },
    HostConfig: { ExtraHosts: ['host.docker.internal:host-gateway'] } };
  await validateKeycloakBackchannelTrust({ root, configuration, inspected });
  for (const modify of [
    value => { value.Mounts[0].RW = true; },
    value => { value.Mounts[0].Source = '/foreign'; },
    value => { value.Config.Env = []; },
    value => { value.HostConfig.ExtraHosts = []; },
  ]) {
    const dirty = structuredClone(inspected); modify(dirty);
    await rejected(validateKeycloakBackchannelTrust({ root, configuration, inspected: dirty }));
  }
  await rejected(initializeKeycloakTrust({ root, configuration: { ...configuration, ca: join(root, 'native.pem') } }));
  assert.deepEqual(await readFile(path), bytes);
  await mkdir(join(directory, 'unexpected'));
  await rejected(initializeKeycloakTrust({ root, configuration }));
});
