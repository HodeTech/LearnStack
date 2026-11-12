import { X509Certificate, createPrivateKey } from 'node:crypto';
import { constants } from 'node:fs';
import { chmod, lstat, mkdir, open, readdir } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseEnv } from 'node:util';
import { refuse } from './development-realm-errors.mjs';

const defaultRoot = fileURLToPath(new URL('../', import.meta.url));
const prefix = 'LEARNSTACK_BFF_BACKCHANNEL_';
const names = ['ENABLED', 'URL', 'BIND_ADDRESS', 'TLS_CERT', 'TLS_KEY', 'CA', 'NATIVE_CA'].map(name => prefix + name);
const sourceNames = [...names, 'LEARNSTACK_BFF_ENABLED', 'LEARNSTACK_BFF_ISSUER',
  'LEARNSTACK_PUBLIC_TLS_CERT', 'LEARNSTACK_PUBLIC_TLS_KEY', 'KEYCLOAK_WEB_CLIENT_SECRET',
  'Identity__AuthorityId', 'Identity__NativeSession__CurrentSecret'];
export const backchannelPath = '/api/auth/backchannel/keycloak';

export function developmentBackchannelUrl(value) {
  try {
    const url = new URL(value);
    if (typeof value !== 'string' || url.href !== value || url.protocol !== 'https:'
      || url.username || url.password || url.search || url.hash || url.pathname !== backchannelPath
      || !url.hostname || !Number.isInteger(Number(url.port || '443'))
      || Number(url.port || '443') < 1 || Number(url.port || '443') > 65535) throw new Error();
    return url;
  } catch { refuse('preflight', 'backchannel_configuration'); }
}

async function ownedFile(path, privateKey = false) {
  let handle;
  try {
    handle = await open(path, constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK);
    const state = await handle.stat();
    if (!state.isFile() || state.uid !== process.getuid() || state.nlink !== 1
      || state.size < 1 || state.size > 256 * 1024 || (state.mode & (privateKey ? 0o077 : 0o022)))
      throw new Error();
    const bytes = Buffer.alloc(256 * 1024 + 1);
    let size = 0;
    while (size < bytes.length) {
      const { bytesRead } = await handle.read(bytes, size, bytes.length - size, size);
      if (!bytesRead) break;
      size += bytesRead;
    }
    if (size !== state.size) throw new Error();
    return bytes.subarray(0, size);
  } catch { refuse('preflight', 'backchannel_configuration'); }
  finally { await handle?.close(); }
}

function publicCertificate(bytes) {
  const text = bytes.toString('utf8');
  if (!/^\s*-----BEGIN CERTIFICATE-----[A-Za-z0-9+/=\r\n]+-----END CERTIFICATE-----\s*$/.test(text))
    refuse('preflight', 'backchannel_configuration');
  return new X509Certificate(bytes);
}

function verifies(leaf, root, hostname) {
  const now = Date.now();
  return root.ca && root.checkIssued(root) && root.verify(root.publicKey)
    && now >= Date.parse(root.validFrom) && now < Date.parse(root.validTo)
    && now >= Date.parse(leaf.validFrom) && now < Date.parse(leaf.validTo)
    && leaf.checkIssued(root) && leaf.verify(root.publicKey)
    && (leaf.checkHost(hostname) || leaf.checkIP(hostname));
}

/** Read-only qualification shared by normal dev, seed, demo and realm setup. */
export async function developmentBackchannelConfiguration({ source, webSource, ambient = process.env, root = defaultRoot }) {
  try {
    if (webSource === undefined) {
      const webPath = join(root, 'frontend/apps/web/.env.local');
      const state = await lstat(webPath).catch(error => { if (error.code !== 'ENOENT') throw error; });
      if (state) webSource = (await ownedFile(webPath, true)).toString('utf8');
    }
    const sources = [source, ...(webSource === undefined ? [] : [webSource])].map(text => {
      if (typeof text !== 'string' || Buffer.byteLength(text) > 256 * 1024 || text.includes('\0')) throw new Error();
      const values = parseEnv(text);
      for (const key of sourceNames) {
        const aliases = [...text.matchAll(new RegExp(`^[ \\t]*(?:export[ \\t]+)?${key}[ \\t]*=`, 'gm'))];
        if (aliases.length > 1 || (aliases.length && !text.match(new RegExp(`^${key}=`, 'm')))) throw new Error();
      }
      return values;
    });
    const values = sources[0];
    for (const key of sourceNames) {
      const present = [...sources.map(value => value[key]), ambient[key]].filter(value => value !== undefined);
      if (present.some(value => value !== present[0])) throw new Error();
    }
    const enabled = values[prefix + 'ENABLED'] ?? 'false';
    if ([...sources, ambient].some(value => value[prefix + 'ENABLED'] !== undefined && value[prefix + 'ENABLED'] !== enabled)) throw new Error();
    if (!['true', 'false'].includes(enabled)) throw new Error();
    if (enabled === 'false') return Object.freeze({ enabled: false });
    if (values.LEARNSTACK_BFF_ENABLED !== 'true') throw new Error();
    for (const key of names) if (!values[key] || /[\r\n\0]/.test(values[key])) throw new Error();
    const url = developmentBackchannelUrl(values[prefix + 'URL']);
    const bindAddress = values[prefix + 'BIND_ADDRESS'];
    if (!['0.0.0.0', '127.0.0.1'].includes(bindAddress)) throw new Error();
    const path = name => resolve(root, values[prefix + name]);
    const certificate = path('TLS_CERT');
    const key = path('TLS_KEY');
    const ca = path('CA');
    const nativeCa = path('NATIVE_CA');
    const [leafBytes, keyBytes, rootBytes, nativeRootBytes, nativeLeafBytes] = await Promise.all([
      ownedFile(certificate), ownedFile(key, true), ownedFile(ca), ownedFile(nativeCa),
      ownedFile(resolve(root, values.LEARNSTACK_PUBLIC_TLS_CERT ?? '.data/tls/public.pem')),
    ]);
    const leaf = publicCertificate(leafBytes);
    if (!leaf.checkPrivateKey(createPrivateKey(keyBytes))
      || !verifies(leaf, publicCertificate(rootBytes), url.hostname)
      || !verifies(publicCertificate(nativeLeafBytes), publicCertificate(nativeRootBytes), 'localhost')) throw new Error();
    return Object.freeze({ enabled: true, url: url.href, bindAddress, certificate, key, ca, nativeCa });
  } catch { refuse('preflight', 'backchannel_configuration'); }
}

async function ownedDirectory(path, mode) {
  try { await mkdir(path, { mode }); await chmod(path, mode); }
  catch (error) { if (error.code !== 'EEXIST') throw error; }
  const state = await lstat(path);
  if (!state.isDirectory() || state.isSymbolicLink() || state.uid !== process.getuid() || (state.mode & 0o022)) throw new Error();
}

/** Mount only a qualified public root, never a certificate key or native CA key. */
export async function initializeKeycloakTrust({ root = defaultRoot, configuration }) {
  try {
    await ownedDirectory(join(root, '.data'), 0o755);
    const directory = join(root, '.data/keycloak-trust');
    await ownedDirectory(directory, 0o755);
    if (((await lstat(directory)).mode & 0o777) !== 0o755) throw new Error();
    const entries = await readdir(directory);
    if (entries.some(name => name !== 'callback-root.pem')) throw new Error();
    const target = join(directory, 'callback-root.pem');
    if (!configuration.enabled) {
      if (entries.length) {
        publicCertificate(await ownedFile(target));
        if (((await lstat(target)).mode & 0o777) !== 0o644) throw new Error();
      }
      return;
    }
    const bytes = await ownedFile(configuration.ca);
    publicCertificate(bytes);
    if (entries.length) {
      if (!(await ownedFile(target)).equals(bytes) || ((await lstat(target)).mode & 0o777) !== 0o644) throw new Error();
      return;
    }
    const handle = await open(target, constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | constants.O_NOFOLLOW, 0o644);
    try { await handle.chmod(0o644); await handle.writeFile(bytes); }
    finally { await handle.close(); }
  } catch { refuse('preflight', 'backchannel_configuration'); }
}

/** Prove the running selected provider uses the qualified host public-root route. */
export async function validateKeycloakBackchannelTrust({ configuration, inspected, root = defaultRoot }) {
  if (!configuration.enabled) return;
  try {
    const directory = join(root, '.data/keycloak-trust');
    const state = await lstat(directory);
    if (!state.isDirectory() || state.isSymbolicLink() || state.uid !== process.getuid() || (state.mode & 0o022)) throw new Error();
    if ((await readdir(directory)).some(name => name !== 'callback-root.pem')) throw new Error();
    const mounts = inspected?.Mounts?.filter(value => value.Destination === '/opt/keycloak/conf/truststores');
    if (mounts?.length !== 1 || mounts[0].Type !== 'bind' || mounts[0].Source !== directory || mounts[0].RW !== false
      || !inspected.Config?.Env?.includes('KC_TRUSTSTORE_PATHS=/opt/keycloak/conf/truststores')
      || !inspected.HostConfig?.ExtraHosts?.includes('host.docker.internal:host-gateway')
      || !(await ownedFile(join(directory, 'callback-root.pem'))).equals(await ownedFile(configuration.ca))) throw new Error();
  } catch { refuse('preflight', 'backchannel_configuration'); }
}
