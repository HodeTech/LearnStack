import { execFile } from 'node:child_process';
import { constants } from 'node:fs';
import { lstat, mkdir, mkdtemp, open, readFile, rename, rm, rmdir } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { realmFailureNotice, refuse } from './development-realm-errors.mjs';

const repositoryRoot = fileURLToPath(new URL('../', import.meta.url));

function executeJoined(args, options) {
  return new Promise((resolve, reject) => {
    let callbackDone = false;
    let closed = false;
    let failure;
    const settle = () => {
      if (!callbackDone || !closed) return;
      if (failure) reject(failure); else resolve();
    };
    // An AbortError callback can precede the child's physical close. Cleanup
    // must wait for both, so a signing process cannot outlive its staging area.
    const child = execFile('openssl', args, options, error => {
      failure = error; callbackDone = true; settle();
    });
    child.once('close', () => { closed = true; settle(); });
  });
}

async function directory(path) {
  await mkdir(path, { mode: 0o700 }).catch(error => { if (error.code !== 'EEXIST') throw error; });
  const state = await lstat(path);
  if (!state.isDirectory() || state.isSymbolicLink() || state.uid !== process.getuid() || (state.mode & 0o022)) throw new Error();
}

/** Explicit operator action; no environment, machine trust or existing files change. */
export async function setupDevelopmentBackchannelTls(root = repositoryRoot) {
  let temporary;
  let reservation;
  let published = false;
  const cancellation = new AbortController();
  const cancel = () => cancellation.abort();
  const destination = join(root, '.data/tls/backchannel');
  for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, cancel);
  try {
    await directory(join(root, '.data'));
    await directory(join(root, '.data/tls'));
    // Existing or partial operator output is never adopted or removed. Build
    // the complete publication outside its destination before reserving it.
    if (await lstat(destination).catch(error => { if (error.code !== 'ENOENT') throw error; })) throw new Error();
    cancellation.signal.throwIfAborted();
    temporary = await mkdtemp(join(root, '.data/tls/.backchannel-setup-'));
    const run = args => executeJoined(args, { cwd: temporary, timeout: 15000,
      maxBuffer: 16 * 1024, env: { PATH: process.env.PATH }, windowsHide: true,
      signal: cancellation.signal });
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
    await rm(join(temporary, 'ca-key.pem'));
    const publication = join(temporary, 'publication');
    await mkdir(publication, { mode: 0o700 });
    for (const name of ['ca.pem', 'leaf.pem', 'leaf-key.pem']) {
      const target = await open(join(publication, name), constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | constants.O_NOFOLLOW, 0o600);
      try { await target.writeFile(await readFile(join(temporary, name))); await target.sync(); }
      finally { await target.close(); }
    }
    cancellation.signal.throwIfAborted();
    await mkdir(destination, { mode: 0o700 });
    reservation = await lstat(destination);
    cancellation.signal.throwIfAborted();
    await rename(publication, destination);
    published = true;
    return destination;
  } catch { refuse('preflight', 'backchannel_configuration'); }
  finally {
    try {
      if (temporary) await rm(temporary, { recursive: true, force: true });
      if (reservation && !published) {
        const state = await lstat(destination).catch(error => { if (error.code !== 'ENOENT') throw error; });
        // rmdir removes only our still-empty reservation; it cannot delete an
        // operator file or an independently replaced directory.
        if (state?.dev === reservation.dev && state.ino === reservation.ino) await rmdir(destination);
      }
    } finally { for (const signal of ['SIGINT', 'SIGTERM']) process.off(signal, cancel); }
  }
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
