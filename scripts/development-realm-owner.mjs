import { createHash, randomUUID } from 'node:crypto';
import { constants } from 'node:fs';
import { lstat, mkdir, open, readFile, unlink } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { parseEnv } from 'node:util';
import { refuse } from './development-realm-errors.mjs';

const privateKeys = ['KEYCLOAK_ADMIN', 'KEYCLOAK_ADMIN_PASSWORD', 'KEYCLOAK_WEB_CLIENT_SECRET'];

export function isLocalDockerEndpoint(value) {
  if (typeof value !== 'string' || !value.startsWith('unix:///')) return false;
  try {
    const endpoint = new URL(value);
    return endpoint.protocol === 'unix:' && endpoint.host === '' && endpoint.pathname.length > 1
      && !endpoint.search && !endpoint.hash;
  } catch { return false; }
}

/** Selected private file is data; inherited credentials never silently override it. */
export function realmEnvironment(source, ambient = process.env) {
  if (typeof source !== 'string' || Buffer.byteLength(source) > 256 * 1024 || source.includes('\0'))
    refuse('preflight', 'configuration');
  const values = parseEnv(source);
  for (const key of privateKeys) {
    const assignments = [...source.matchAll(new RegExp(`^${key}=`, 'gm'))];
    const aliases = [...source.matchAll(new RegExp(`^[ \\t]*(?:export[ \\t]+)?${key}[ \\t]*=`, 'gm'))];
    if (assignments.length !== 1 || aliases.length !== 1 || !values[key] || /[\r\n\0]/.test(values[key]))
      refuse('preflight', key === 'KEYCLOAK_WEB_CLIENT_SECRET' ? 'missing_web_secret' : 'configuration');
    if (ambient[key] !== undefined && ambient[key] !== values[key]) refuse('preflight', 'configuration');
  }
  return Object.freeze(Object.fromEntries(privateKeys.map((key) => [key, values[key]])));
}

/** One exclusive local owner, never reclaimed from age or a guessed process state. */
export async function acquireRealmLock(identity, directory = join(tmpdir(), `learnstack-realm-${process.getuid()}`)) {
  if (typeof identity !== 'string' || !identity || Buffer.byteLength(identity) > 4096)
    refuse('lock', 'configuration');
  await mkdir(directory, { mode: 0o700 }).catch((error) => {
    if (error.code !== 'EEXIST') refuse('lock', 'configuration');
  });
  const parent = await lstat(directory);
  if (!parent.isDirectory() || parent.isSymbolicLink() || parent.uid !== process.getuid()
      || (parent.mode & 0o777) !== 0o700) refuse('lock', 'configuration');
  const path = join(directory, createHash('sha256').update(identity).digest('hex') + '.lock');
  let handle;
  try { handle = await open(path, constants.O_CREAT | constants.O_EXCL | constants.O_WRONLY | constants.O_NOFOLLOW, 0o600); }
  catch (error) { refuse('lock', error.code === 'EEXIST' ? 'lock_busy' : 'configuration'); }
  let nonce;
  let owned;
  try {
    try {
      nonce = randomUUID();
      await handle.writeFile(nonce, 'utf8');
      owned = await handle.stat();
    } finally { await handle.close(); }
  } catch {
    await unlink(path).catch(() => refuse('cleanup', 'cleanup'));
    refuse('lock', 'configuration');
  }
  let released = false;
  return Object.freeze({
    release: async () => {
      if (released) refuse('cleanup', 'cleanup');
      const current = await lstat(path);
      if (!current.isFile() || current.isSymbolicLink() || current.dev !== owned.dev
          || current.ino !== owned.ino || current.nlink !== 1 || current.uid !== owned.uid
          || current.size !== Buffer.byteLength(nonce) || (current.mode & 0o777) !== 0o600
          || await readFile(path, 'utf8') !== nonce) refuse('cleanup', 'cleanup');
      await unlink(path);
      released = true;
    },
  });
}

/** Only the selected running Compose service may receive the private admin credential. */
export function validateRealmTarget(selected, model, containers, inspected, context, values) {
  const service = model?.services?.keycloak;
  const image = 'quay.io/keycloak/keycloak:26.6.2';
  if (model?.name !== selected.project || service?.image !== image
      || !Array.isArray(containers) || containers.length !== 1 || typeof containers[0] !== 'string'
      || !/^[a-f0-9]{64}$/.test(containers[0]) || inspected?.Id !== containers[0]
      || inspected?.Config?.Image !== image || inspected?.State?.Running !== true
      || inspected?.Config?.Labels?.['com.docker.compose.project'] !== selected.project
      || inspected?.Config?.Labels?.['com.docker.compose.service'] !== 'keycloak'
      || !Array.isArray(context) || context.length !== 1
      || !isLocalDockerEndpoint(context[0]?.Endpoints?.docker?.Host))
    refuse('preflight', 'configuration');
  const ports = inspected.NetworkSettings?.Ports?.['8080/tcp'];
  if (!Array.isArray(ports) || ports.length !== 1 || ports[0].HostIp !== '127.0.0.1'
      || ports[0].HostPort !== '8080') refuse('preflight', 'configuration');
  const environment = inspected.Config.Env;
  if (!Array.isArray(environment) || environment.some(entry => typeof entry !== 'string'))
    refuse('preflight', 'configuration');
  for (const key of privateKeys) {
    const entries = environment.filter((entry) => entry.startsWith(key + '='));
    if (entries.length !== 1 || entries[0] !== `${key}=${values[key]}`
        || service.environment?.[key] !== values[key]) refuse('preflight', 'configuration');
  }
  return Object.freeze({ baseUrl: 'http://localhost:8080/',
    expectedIssuer: 'http://localhost:8080/realms/learnstack',
    lockIdentity: containers[0] + '/realms/learnstack' });
}
