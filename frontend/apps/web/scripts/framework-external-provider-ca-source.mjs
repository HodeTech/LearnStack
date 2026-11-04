// Fixture-only copied-owner source transform for the real provider's owned CA.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import {
  closeSync,
  constants,
  fstatSync,
  lstatSync,
  openSync,
  readFileSync,
  realpathSync,
  writeFileSync,
} from 'node:fs';
import { join, resolve } from 'node:path';

const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex');
const refuse = () => {
  throw new Error('External provider trust refused');
};
const replaceOnce = (source, anchor, replacement) => {
  assert.equal(source.split(anchor).length, 2, 'Unique copied external trust anchor required');
  return source.replace(anchor, replacement);
};

function ownedRoot(root) {
  const state = lstatSync(root);
  if (
    !state.isDirectory() ||
    state.isSymbolicLink() ||
    state.uid !== process.getuid() ||
    (state.mode & 0o777) !== 0o700 ||
    realpathSync(root) !== root
  )
    refuse();
}

function exactOwnedCa(path, expectedHash) {
  const before = lstatSync(path, { bigint: true });
  if (
    !before.isFile() ||
    before.isSymbolicLink() ||
    before.nlink !== 1n ||
    before.uid !== BigInt(process.getuid()) ||
    (before.mode & 0o777n) !== 0o600n ||
    before.size <= 0n ||
    before.size >= 8192n ||
    realpathSync(path) !== path ||
    !constants.O_NOFOLLOW
  )
    refuse();
  const descriptor = openSync(path, constants.O_RDONLY | constants.O_NOFOLLOW);
  try {
    const opened = fstatSync(descriptor, { bigint: true });
    if (
      !opened.isFile() ||
      opened.dev !== before.dev ||
      opened.ino !== before.ino ||
      opened.nlink !== 1n ||
      opened.uid !== before.uid ||
      (opened.mode & 0o777n) !== 0o600n ||
      opened.size !== before.size ||
      opened.mtimeNs !== before.mtimeNs ||
      opened.ctimeNs !== before.ctimeNs
    )
      refuse();
    const bytes = readFileSync(descriptor);
    const after = fstatSync(descriptor, { bigint: true });
    const named = lstatSync(path, { bigint: true });
    if (
      !bytes.length ||
      BigInt(bytes.length) !== opened.size ||
      after.size !== opened.size ||
      after.mtimeNs !== opened.mtimeNs ||
      after.ctimeNs !== opened.ctimeNs ||
      !named.isFile() ||
      named.isSymbolicLink() ||
      named.dev !== opened.dev ||
      named.ino !== opened.ino ||
      named.nlink !== 1n ||
      named.uid !== opened.uid ||
      named.mode !== opened.mode ||
      named.size !== opened.size ||
      named.mtimeNs !== opened.mtimeNs ||
      named.ctimeNs !== opened.ctimeNs ||
      realpathSync(path) !== path ||
      sha256(bytes) !== expectedHash
    )
      refuse();
    return bytes;
  } finally {
    closeSync(descriptor);
  }
}

export function installCopiedExternalProviderCa({ root, app, authority }) {
  const privateRoot = realpathSync(root);
  const appRoot = realpathSync(app);
  if (
    typeof authority !== 'string' ||
    !authority.startsWith('-----BEGIN CERTIFICATE-----') ||
    Buffer.byteLength(authority) >= 8192 ||
    resolve(appRoot, '../../..') !== privateRoot
  )
    refuse();
  ownedRoot(privateRoot);
  const path = join(privateRoot, 'provider-ca.pem');
  const original = Buffer.from(authority, 'utf8');
  // Exclusive creation keeps a prior or aliased CA from becoming fixture input.
  writeFileSync(path, original, { flag: 'wx', mode: 0o600 });
  const expectedHash = sha256(original);
  if (!exactOwnedCa(path, expectedHash).equals(original)) refuse();

  const ownerPath = join(appRoot, 'scripts/public-server.mjs');
  let owner = readFileSync(ownerPath, 'utf8');
  assert.equal(
    owner.match(/createCopiedParentProof\(/g)?.length,
    1,
    'Only one copied proof profile call',
  );
  assert.equal(
    owner.match(/^[ ]{2}copiedProof = createCopiedParentProof\('none'\);$/gm)?.length,
    1,
    'Only external copied owner',
  );
  owner = replaceOnce(
    owner,
    "import { realpathSync } from 'node:fs';",
    `import { createHash } from 'node:crypto';
import { closeSync, constants, fstatSync, lstatSync, openSync, readFileSync, realpathSync } from 'node:fs';
import { join, resolve } from 'node:path';`,
  );
  owner = replaceOnce(
    owner,
    '  const environment = frameworkRuntimeEnvironment(identity, mode);',
    `  const environment = frameworkRuntimeEnvironment(identity, mode);
  // The external fixture supplied this exact CA before the genuine build.
  // Node consumes NODE_EXTRA_CA_CERTS only when the private child starts.
  {
    const privateRoot = resolve(appRoot, '../../..');
    const rootState = lstatSync(privateRoot);
    if (!rootState.isDirectory() || rootState.isSymbolicLink() ||
        rootState.uid !== process.getuid() || (rootState.mode & 0o777) !== 0o700 ||
        realpathSync(privateRoot) !== privateRoot) throw new Error('External provider trust refused');
    const caPath = join(privateRoot, 'provider-ca.pem');
    const before = lstatSync(caPath, { bigint: true });
    if (!before.isFile() || before.isSymbolicLink() || before.nlink !== 1n ||
        before.uid !== BigInt(process.getuid()) || (before.mode & 0o777n) !== 0o600n ||
        before.size <= 0n || before.size >= 8192n || realpathSync(caPath) !== caPath ||
        !constants.O_NOFOLLOW) throw new Error('External provider trust refused');
    const descriptor = openSync(caPath, constants.O_RDONLY | constants.O_NOFOLLOW);
    try {
      const opened = fstatSync(descriptor, { bigint: true });
      if (!opened.isFile() || opened.dev !== before.dev || opened.ino !== before.ino ||
          opened.nlink !== 1n || opened.uid !== before.uid ||
          (opened.mode & 0o777n) !== 0o600n || opened.size !== before.size ||
          opened.mtimeNs !== before.mtimeNs || opened.ctimeNs !== before.ctimeNs)
        throw new Error('External provider trust refused');
      const bytes = readFileSync(descriptor);
      const after = fstatSync(descriptor, { bigint: true });
      const named = lstatSync(caPath, { bigint: true });
      if (BigInt(bytes.length) !== opened.size || after.size !== opened.size ||
          after.mtimeNs !== opened.mtimeNs || after.ctimeNs !== opened.ctimeNs ||
          !named.isFile() || named.isSymbolicLink() || named.dev !== opened.dev ||
          named.ino !== opened.ino || named.nlink !== 1n || named.uid !== opened.uid ||
          named.mode !== opened.mode || named.size !== opened.size ||
          named.mtimeNs !== opened.mtimeNs || named.ctimeNs !== opened.ctimeNs ||
          realpathSync(caPath) !== caPath ||
          createHash('sha256').update(bytes).digest('hex') !== '${expectedHash}')
        throw new Error('External provider trust refused');
    } finally {
      closeSync(descriptor);
    }
    environment.NODE_EXTRA_CA_CERTS = caPath;
  }`,
  );
  writeFileSync(ownerPath, owner);
  return Object.freeze({ caPath: path, sha256: expectedHash, ownerPath });
}
