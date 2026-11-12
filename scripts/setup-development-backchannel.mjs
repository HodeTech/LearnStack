import { execFile } from 'node:child_process';
import { constants } from 'node:fs';
import { lstat, mkdir, mkdtemp, open, readFile, rm } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { realmFailureNotice, refuse } from './development-realm-errors.mjs';

const execute = promisify(execFile);
const repositoryRoot = fileURLToPath(new URL('../', import.meta.url));

async function directory(path) {
  await mkdir(path, { mode: 0o700 }).catch(error => { if (error.code !== 'EEXIST') throw error; });
  const state = await lstat(path);
  if (!state.isDirectory() || state.isSymbolicLink() || state.uid !== process.getuid() || (state.mode & 0o022)) throw new Error();
}

/** Explicit operator action; no environment, machine trust or existing files change. */
export async function setupDevelopmentBackchannelTls(root = repositoryRoot) {
  let temporary;
  try {
    await directory(join(root, '.data'));
    await directory(join(root, '.data/tls'));
    const destination = join(root, '.data/tls/backchannel');
    // One exclusive directory owns all outputs. Partial or existing material
    // requires operator inspection, never replacement or implicit key rotation.
    await mkdir(destination, { mode: 0o700 });
    temporary = await mkdtemp(join(destination, '.setup-'));
    const run = args => execute('openssl', args, { cwd: temporary, timeout: 15000,
      maxBuffer: 16 * 1024, env: { PATH: process.env.PATH }, windowsHide: true });
    await run(['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-sha256', '-days', '365',
      '-subj', '/CN=LearnStack development callback root', '-keyout', 'ca-key.pem', '-out', 'ca.pem',
      '-addext', 'basicConstraints=critical,CA:TRUE', '-addext', 'keyUsage=critical,keyCertSign,cRLSign']);
    await run(['req', '-newkey', 'rsa:2048', '-nodes', '-sha256', '-subj', '/CN=host.docker.internal',
      '-keyout', 'leaf-key.pem', '-out', 'leaf.csr']);
    const extensions = await open(join(temporary, 'extensions.cnf'), 'wx', 0o600);
    try { await extensions.writeFile('subjectAltName=DNS:host.docker.internal\nbasicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\n'); }
    finally { await extensions.close(); }
    await run(['x509', '-req', '-in', 'leaf.csr', '-CA', 'ca.pem', '-CAkey', 'ca-key.pem',
      '-CAcreateserial', '-days', '365', '-sha256', '-extfile', 'extensions.cnf', '-out', 'leaf.pem']);
    for (const name of ['ca.pem', 'leaf.pem', 'leaf-key.pem']) {
      const target = await open(join(destination, name), constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | constants.O_NOFOLLOW, 0o600);
      try { await target.writeFile(await readFile(join(temporary, name))); }
      finally { await target.close(); }
    }
    return destination;
  } catch { refuse('preflight', 'backchannel_configuration'); }
  finally { if (temporary) await rm(temporary, { recursive: true, force: true }); }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    if (process.argv.length !== 2) refuse('preflight', 'backchannel_configuration');
    await setupDevelopmentBackchannelTls();
    console.log('backchannel: created owned callback leaf/key/public root; configure explicit values using infra/keycloak/README.md#native-back-channel-logout');
  } catch (error) {
    console.error(realmFailureNotice(error));
    process.exitCode = 1;
  }
}
