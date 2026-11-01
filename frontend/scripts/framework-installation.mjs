// Builtins only: this module runs before application, SDK or framework code.
import { execFileSync } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import {
  closeSync,
  cpSync,
  existsSync,
  fstatSync,
  lstatSync,
  mkdirSync,
  openSync,
  readFileSync,
  readdirSync,
  realpathSync,
  renameSync,
  rmSync,
  rmdirSync,
  statSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs';
import { createRequire } from 'node:module';
import { delimiter, dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  assertFrameworkProcessFamilySupport,
  runFrameworkProcessFamily,
} from './framework-process-family.mjs';

export const FRAMEWORK_FORMAT = 1;
export const FRAMEWORK_FRONTEND_ROOT = realpathSync(fileURLToPath(new URL('../', import.meta.url)));
const CENSUS = 'framework/next-response-cleanup/census.json';
const MANIFEST = 'framework-build.json';
const DEVELOPMENT = '.framework-state/development.json';
const SHA256 = /^[0-9a-f]{64}$/;
const CODE_EXTENSIONS = /\.(?:[cm]?js|json|map|node|wasm)$/;
const APP_CONFIG_FILES = [
  'package.json',
  'next.config.ts',
  'next-env.d.ts',
  'tsconfig.json',
  'tsconfig.server.json',
  'postcss.config.cjs',
  'tailwind.config.ts',
];
const OPTIONAL_APP_CONFIG_FILES = ['.eslintrc.cjs', '.eslintignore'];
// These are the competing discovery paths of the qualified Next/PostCSS/
// Tailwind/ESLint loaders. They cannot silently outrank the reviewed files.
const UNQUALIFIED_APP_CONFIG_FILES = new Set([
  'next.config.js',
  'next.config.mjs',
  'next.config.cjs',
  'next.config.json',
  'next.config.mts',
  'next.config.cts',
  '.postcssrc',
  '.postcssrc.json',
  '.postcssrc.js',
  '.postcssrc.yml',
  '.postcssrc.yaml',
  'postcss.config.json',
  'postcss.config.js',
  'postcss.config.mjs',
  'postcss.config.ts',
  'postcss.config.cts',
  'postcss.config.mts',
  'tailwind.config.js',
  'tailwind.config.cjs',
  'tailwind.config.mjs',
  'tailwind.config.cts',
  'tailwind.config.mts',
  '.babelrc',
  '.babelrc.json',
  '.babelrc.js',
  '.babelrc.mjs',
  '.babelrc.cjs',
  'babel.config.js',
  'babel.config.json',
  'babel.config.mjs',
  'babel.config.cjs',
  'babel.config.ts',
  'babel.config.cts',
  'babel.config.mts',
  'eslint.config.js',
  'eslint.config.mjs',
  'eslint.config.cjs',
  'eslint.config.ts',
  'eslint.config.mts',
  'eslint.config.cts',
  '.eslintrc.js',
  '.eslintrc.yaml',
  '.eslintrc.yml',
  '.eslintrc.json',
  '.eslintrc',
  '.browserslistrc',
  'browserslist',
  'jsconfig.json',
]);
function assertQualifiedConfiguration(appRoot, appManifest) {
  if (
    readdirSync(appRoot).some((name) => UNQUALIFIED_APP_CONFIG_FILES.has(name)) ||
    ['babel', 'postcss', 'eslintConfig', 'browserslist'].some((name) =>
      Object.hasOwn(appManifest, name),
    )
  )
    refuse('configuration_variant');
}
const LOCAL_RUNTIME_ENVIRONMENT = new Set([
  'DEBUG',
  'LEARNSTACK_PUBLIC_API_ORIGIN',
  'LEARNSTACK_PUBLIC_HOP_SECRET',
  'LEARNSTACK_PUBLIC_TLS_CERT',
  'LEARNSTACK_PUBLIC_TLS_KEY',
  'LEARNSTACK_BFF_ENABLED',
  'LEARNSTACK_BFF_ISSUER',
  'KEYCLOAK_WEB_CLIENT_SECRET',
  'Identity__AuthorityId',
  'Identity__NativeSession__CurrentSecret',
]);
const OUTER_FRAMEWORK_ENVIRONMENT = new Set([
  'NEXT_TELEMETRY_DISABLED',
  'NEXT_TRACE_SPAN_THRESHOLD_MS',
  'SWC_NATIVE_BINDING_CACHE',
  'BROWSERSLIST',
]);
// This is pinned Next's already-qualified default. Supplying the exact query
// prevents Browserslist from searching unowned parent/home configuration.
const QUALIFIED_BROWSERSLIST = 'chrome 64, edge 79, firefox 67, opera 51, safari 12';
// Compiler subprocesses never inherit the ambient application/package-manager
// environment. OS transport locations are not compiler option selectors.
// HOME is intentionally absent: an unavailable private native cache must not
// redirect materialization into an inherited user cache outside the app owner.
const PROCESS_ENVIRONMENT_KEYS = ['PATH', 'TMPDIR', 'TMP', 'TEMP'];
const DISALLOWED_BUILD_ENVIRONMENT = [
  'NODE_OPTIONS',
  'NEXT_PRIVATE_BUILD_WORKER',
  'NEXT_PRIVATE_STANDALONE',
  'NEXT_PRIVATE_LOCAL_WEBPACK',
  'NEXT_BUILD_ID',
  'NEXT_DIST_DIR',
  'TURBOPACK',
];

export class FrameworkAdmissionError extends Error {
  constructor(reason) {
    super(`Framework admission refused: ${reason}`);
    this.name = 'FrameworkAdmissionError';
    this.reason = reason;
  }
}
function refuse(reason) {
  throw new FrameworkAdmissionError(reason);
}
function sha256(value) {
  return createHash('sha256').update(value).digest('hex');
}
function canonical(value) {
  return JSON.stringify(value);
}
function canonicalPath(root, path) {
  if (typeof path !== 'string' || !path || path.includes('\\') || isAbsolute(path))
    refuse('subject_path');
  const full = resolve(root, path);
  if (relative(root, full).startsWith(`..${sep}`) || full === root || relative(root, full) === '..')
    refuse('subject_path');
  return full;
}
function regularBytes(path, maximum = 128 * 1024 * 1024) {
  let descriptor;
  try {
    // O_NOFOLLOW is not portable to every supported Windows surface; the lstat
    // and descriptor identity checks also reject aliases on those platforms.
    const before = lstatSync(path);
    if (!before.isFile() || before.isSymbolicLink() || before.size > maximum)
      refuse('subject_file');
    descriptor = openSync(path, 'r');
    const opened = fstatSync(descriptor);
    if (
      !opened.isFile() ||
      opened.dev !== before.dev ||
      opened.ino !== before.ino ||
      opened.size !== before.size
    )
      refuse('subject_file');
    const bytes = readFileSync(descriptor);
    const after = fstatSync(descriptor);
    if (
      after.size !== bytes.length ||
      after.mtimeMs !== opened.mtimeMs ||
      after.ctimeMs !== opened.ctimeMs
    )
      refuse('subject_changed');
    return bytes;
  } catch (error) {
    if (error instanceof FrameworkAdmissionError) throw error;
    refuse('subject_file');
  } finally {
    if (descriptor !== undefined) closeSync(descriptor);
  }
}
function jsonFile(path, maximum = 8 * 1024 * 1024) {
  try {
    return JSON.parse(regularBytes(path, maximum).toString('utf8'));
  } catch (error) {
    if (error instanceof FrameworkAdmissionError) throw error;
    refuse('subject_json');
  }
}
function packageRoot(appRoot, name) {
  try {
    // Workspace packages intentionally do not export package.json. Resolve the
    // same direct package link Node uses before following the package's exports.
    const packagePath = realpathSync(join(appRoot, 'node_modules', name, 'package.json'));
    const manifest = jsonFile(packagePath);
    if (manifest.name !== name) refuse('package_resolution');
    return dirname(packagePath);
  } catch {
    refuse('package_resolution');
  }
}
function inside(path, root) {
  const part = relative(root, path);
  return part === '' || (!part.startsWith(`..${sep}`) && part !== '..' && !isAbsolute(part));
}
function checkedRoot(root) {
  if (!isAbsolute(root)) refuse('root');
  try {
    const path = realpathSync(root);
    if (!statSync(path).isDirectory()) refuse('root');
    return path;
  } catch {
    refuse('root');
  }
}
function assertNode(tuple) {
  if (
    !tuple ||
    process.versions.node !== tuple.node ||
    typeof tuple.pnpm !== 'string' ||
    typeof tuple.next !== 'string'
  )
    refuse('node_version');
}

// pnpm 9.12.3's patch identity is base32(MD5(normalized UTF-8)). SHA-256
// independently authenticates the carrier; the MD5 value is only a lock key.
export function pnpmPatchHash(text) {
  const bytes = createHash('md5').update(text.replaceAll('\r\n', '\n')).digest();
  const alphabet = 'abcdefghijklmnopqrstuvwxyz234567';
  let buffer = 0;
  let bits = 0;
  let output = '';
  for (const byte of bytes) {
    buffer = (buffer << 8) | byte;
    bits += 8;
    while (bits >= 5) {
      bits -= 5;
      output += alphabet[(buffer >>> bits) & 31];
    }
  }
  if (bits) output += alphabet[(buffer << (5 - bits)) & 31];
  return output;
}
function lockPatch(lock, key) {
  // A deliberately closed parser for pnpm 9's generated two-field top-level
  // patch block, rather than loading a dependency before admission.
  const lines = lock.split(/\r?\n/);
  const start = lines.indexOf('patchedDependencies:');
  if (start < 0 || lines.filter((line) => line === 'patchedDependencies:').length !== 1)
    refuse('patch_lock');
  const entries = new Map();
  let current;
  for (let at = start + 1; at < lines.length; at++) {
    const line = lines[at];
    if (!line.trim()) continue;
    if (!line.startsWith(' ')) break;
    const entry = /^  (?:'([^']+)'|"([^"]+)"|([^ :]+)):$/.exec(line);
    if (entry) {
      current = entry[1] ?? entry[2] ?? entry[3];
      if (entries.has(current)) refuse('patch_lock');
      entries.set(current, {});
      continue;
    }
    const field = /^    (hash|path): (?:'([^']*)'|"([^"]*)"|([^\s]+))$/.exec(line);
    if (!field || !current || entries.get(current)[field[1]] !== undefined) refuse('patch_lock');
    entries.get(current)[field[1]] = field[2] ?? field[3] ?? field[4];
  }
  if (entries.size !== 1 || !entries.has(key)) refuse('patch_lock');
  return entries.get(key);
}
function censusIdentity(frontendRoot) {
  const path = join(frontendRoot, CENSUS);
  const bytes = regularBytes(path, 16 * 1024 * 1024);
  let census;
  try {
    census = JSON.parse(bytes);
  } catch {
    refuse('census_json');
  }
  if (
    census.schemaVersion !== FRAMEWORK_FORMAT ||
    !Array.isArray(census.files) ||
    census.files.length !== 59 ||
    !Array.isArray(census.absentPaths) ||
    !census.generator ||
    !census.patch
  )
    refuse('census_shape');
  assertNode(census.tuple);
  let declaration;
  try {
    declaration = regularBytes(join(frontendRoot, '.nvmrc'), 128).toString('utf8').trim();
  } catch {
    refuse('node_declaration');
  }
  if (declaration !== census.tuple.node) refuse('node_declaration');
  return { census, censusSha256: sha256(bytes) };
}
export function verifyFrameworkInstallation({
  appRoot = process.cwd(),
  frontendRoot = FRAMEWORK_FRONTEND_ROOT,
  distribution = false,
} = {}) {
  const began = performance.now();
  appRoot = checkedRoot(appRoot);
  frontendRoot = checkedRoot(frontendRoot);
  const { census, censusSha256 } = censusIdentity(frontendRoot);
  const manifest = jsonFile(join(frontendRoot, 'package.json'));
  const appManifest = jsonFile(join(appRoot, 'package.json'));
  assertQualifiedConfiguration(appRoot, appManifest);
  const expectedPatch = `next@${census.tuple.next}`;
  if (
    manifest.packageManager !== `pnpm@${census.tuple.pnpm}` ||
    manifest.engines?.node !== census.tuple.node ||
    manifest.engines?.pnpm !== census.tuple.pnpm ||
    appManifest.dependencies?.next !== census.tuple.next ||
    manifest.pnpm?.patchedDependencies?.[expectedPatch] !== census.patch.path ||
    Object.keys(manifest.pnpm?.patchedDependencies ?? {}).length !== 1 ||
    manifest.pnpm?.allowNonAppliedPatches === true
  )
    refuse('package_declaration');
  const patch = regularBytes(canonicalPath(frontendRoot, census.patch.path));
  const patchHash = pnpmPatchHash(patch.toString('utf8'));
  const declared = lockPatch(
    regularBytes(join(frontendRoot, 'pnpm-lock.yaml'), 8 * 1024 * 1024).toString('utf8'),
    expectedPatch,
  );
  if (
    !SHA256.test(census.patch.sha256) ||
    sha256(patch) !== census.patch.sha256 ||
    patchHash !== census.patch.pnpmHash ||
    declared.path !== census.patch.path ||
    declared.hash !== patchHash
  )
    refuse('patch_identity');
  const generatorFiles = [census.generator, ...(census.generator.inputs ?? [])];
  for (const subject of generatorFiles) {
    if (
      !SHA256.test(subject.sha256) ||
      sha256(regularBytes(canonicalPath(frontendRoot, subject.path))) !== subject.sha256
    )
      refuse('generator_identity');
  }
  const nextRoot = packageRoot(appRoot, 'next');
  const storeRoot = realpathSync(join(frontendRoot, 'node_modules'));
  if (!inside(nextRoot, storeRoot)) refuse('next_resolution');
  const metadata = census.packageMetadata;
  const packageBytes = regularBytes(join(nextRoot, 'package.json'), 64 * 1024);
  if (
    metadata?.path !== 'package.json' ||
    metadata.runtimeEntry !== 'dist/server/next.js' ||
    metadata.compilerEntry !== 'dist/bin/next' ||
    !SHA256.test(metadata.sha256) ||
    metadata.bytes !== packageBytes.length ||
    metadata.sha256 !== sha256(packageBytes)
  )
    refuse('next_metadata');
  const nextPackage = JSON.parse(packageBytes.toString('utf8'));
  if (
    nextPackage.name !== 'next' ||
    nextPackage.version !== census.tuple.next ||
    nextPackage.main !== `./${metadata.runtimeEntry}` ||
    nextPackage.bin?.next !== `./${metadata.compilerEntry}` ||
    Object.hasOwn(nextPackage, 'exports')
  )
    refuse('next_metadata');
  const require = createRequire(join(appRoot, 'package.json'));
  try {
    // This qualified package has no exports map. Node's ESM legacy-main and
    // CommonJS entry selection therefore share this exact checked main file.
    for (const [actual, expected] of [
      [require.resolve('next'), metadata.runtimeEntry],
      [require.resolve('next/dist/bin/next'), metadata.compilerEntry],
    ]) {
      const target = canonicalPath(nextRoot, expected);
      if (realpathSync(target) !== target || realpathSync(actual) !== target)
        refuse('next_resolution');
    }
  } catch (error) {
    if (error instanceof FrameworkAdmissionError) throw error;
    refuse('next_resolution');
  }
  const subjects = new Set();
  let bytesRead = 0;
  for (const subject of census.files) {
    if (
      subjects.has(subject.path) ||
      !SHA256.test(subject.sha256) ||
      !Number.isSafeInteger(subject.bytes) ||
      subject.bytes < 0
    )
      refuse('census_subject');
    subjects.add(subject.path);
    const path = canonicalPath(nextRoot, subject.path);
    // Reject a symlink in any subject ancestor, not just a symlinked leaf.
    if (realpathSync(path) !== path) refuse('subject_alias');
    const actual = regularBytes(path);
    if (actual.length !== subject.bytes || sha256(actual) !== subject.sha256)
      refuse('installed_bytes');
    bytesRead += actual.length;
  }
  for (const absent of census.absentPaths) {
    if (subjects.has(absent) || existsSync(canonicalPath(nextRoot, absent))) refuse('obsolete_map');
  }
  if (distribution) {
    const actual = distributionPaths(nextRoot);
    if (
      !Array.isArray(census.distribution?.paths) ||
      canonical(actual) !== canonical(census.distribution.paths)
    )
      refuse('distribution_membership');
  }
  return Object.freeze({
    format: FRAMEWORK_FORMAT,
    tuple: Object.freeze({ ...census.tuple }),
    appRoot,
    frontendRoot,
    nextRoot,
    censusSha256,
    patchSha256: census.patch.sha256,
    generatorSha256: census.generator.sha256,
    installed: Object.freeze({
      files: subjects.size,
      bytes: bytesRead,
      milliseconds: performance.now() - began,
    }),
  });
}

function executableOnPath(name, pathValue = process.env.PATH ?? '') {
  if (name.includes('/') || name.includes('\\')) return realpathSync(resolve(name));
  for (const directory of pathValue.split(delimiter)) {
    if (!directory || !isAbsolute(directory)) continue;
    const candidate = join(directory, name);
    try {
      if (statSync(candidate).isFile()) return realpathSync(candidate);
    } catch {
      /* next PATH entry */
    }
  }
  refuse('manager_missing');
}
function managerFiles(frontendRoot) {
  const metadata = jsonFile(join(frontendRoot, 'framework/next-response-cleanup/pnpm.json'), 8192);
  const { census } = censusIdentity(frontendRoot);
  if (
    canonical(metadata) !== canonical(census.manager) ||
    metadata.version !== census.tuple.pnpm ||
    !Array.isArray(metadata.files) ||
    metadata.files.length !== 3 ||
    new Set(metadata.files.map((file) => file.path)).size !== 3
  )
    refuse('manager_metadata');
  return metadata;
}
function verifiedManagerCLI(frontendRoot, executable, expected) {
  let path;
  try {
    path = realpathSync(executable);
  } catch {
    refuse('manager_missing');
  }
  const root = dirname(dirname(path));
  if (relative(root, path).replaceAll('\\', '/') !== 'bin/pnpm.cjs') refuse('manager_binary');
  const metadata = managerFiles(frontendRoot);
  if (metadata.version !== expected) refuse('manager_metadata');
  for (const file of metadata.files) {
    if (
      !SHA256.test(file.sha256) ||
      !['package.json', 'bin/pnpm.cjs', 'dist/pnpm.cjs'].includes(file.path)
    )
      refuse('manager_metadata');
    const bytes = regularBytes(canonicalPath(root, file.path), 32 * 1024 * 1024);
    if (bytes.length !== file.bytes || sha256(bytes) !== file.sha256) refuse('manager_binary');
  }
  const version = execFileSync(process.execPath, [path, '--version'], {
    cwd: frontendRoot,
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
    timeout: 15_000,
    maxBuffer: 4096,
  }).trim();
  if (version !== expected) refuse('manager_version');
  return Object.freeze({
    command: process.execPath,
    args: Object.freeze([path]),
    executable: path,
    version,
    sha256: sha256(regularBytes(path)),
    metadataSha256: sha256(
      regularBytes(join(frontendRoot, 'framework/next-response-cleanup/pnpm.json')),
    ),
  });
}
export function resolveQualifiedPnpm({
  frontendRoot = FRAMEWORK_FRONTEND_ROOT,
  executable,
  lifecycle = false,
} = {}) {
  frontendRoot = checkedRoot(frontendRoot);
  const { census } = censusIdentity(frontendRoot);
  if (lifecycle) return verifiedManagerCLI(frontendRoot, executable, census.tuple.pnpm);
  let selected;
  let actual;
  try {
    selected = executableOnPath(executable ?? 'pnpm');
    // Corepack is a selector, not the qualified producer. Ask the selected
    // manager to run a builtins-only script exposing its own npm_execpath,
    // then authenticate and invoke those actual pnpm bytes directly.
    actual = execFileSync(selected, ['--silent', 'run', 'framework:manager'], {
      cwd: frontendRoot,
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'pipe'],
      timeout: 15_000,
      maxBuffer: 4096,
    }).trim();
  } catch {
    refuse('manager_probe');
  }
  if (!isAbsolute(actual) || actual.includes('\n') || actual.includes('\r'))
    refuse('manager_probe');
  return verifiedManagerCLI(frontendRoot, actual, census.tuple.pnpm);
}

function distributionPaths(root) {
  const result = [];
  function visit(directory, prefix) {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const name = prefix ? `${prefix}/${entry.name}` : entry.name;
      if (entry.isDirectory()) visit(join(directory, entry.name), name);
      else if (entry.isFile()) result.push(name);
      else refuse('distribution_file');
    }
  }
  visit(root, '');
  return result.sort();
}
function subjectFiles(root, { extensions, exclude = [] } = {}) {
  const result = [];
  function visit(directory, prefix) {
    for (const entry of readdirSync(directory, { withFileTypes: true }).sort((a, b) =>
      a.name.localeCompare(b.name, 'en'),
    )) {
      const name = prefix ? `${prefix}/${entry.name}` : entry.name;
      if (exclude.some((pattern) => pattern.test(name))) continue;
      const path = join(directory, entry.name);
      if (entry.isSymbolicLink()) refuse('graph_alias');
      if (entry.isDirectory()) visit(path, name);
      else if (entry.isFile() && (!extensions || extensions.test(name))) {
        const bytes = regularBytes(path);
        result.push({ path: name, bytes: bytes.length, sha256: sha256(bytes) });
      } else if (!entry.isFile()) refuse('graph_file');
    }
  }
  if (existsSync(root)) visit(root, '');
  return result;
}
function prefixed(prefix, values) {
  return values.map((value) => ({ ...value, path: `${prefix}/${value.path}` }));
}
function inputFile(prefix, root, name, optional = false) {
  const path = join(root, name);
  if (!existsSync(path) && optional) return [];
  const bytes = regularBytes(path);
  return [{ path: `${prefix}/${name}`, bytes: bytes.length, sha256: sha256(bytes) }];
}
export function frameworkInputGraph(identity, { development = false } = {}) {
  const { appRoot, frontendRoot } = identity;
  const sdkRoot = packageRoot(appRoot, '@learnstack/sdk');
  const uiRoot = packageRoot(appRoot, '@learnstack/ui');
  const configRoot = packageRoot(appRoot, '@learnstack/config');
  assertQualifiedConfiguration(appRoot, jsonFile(join(appRoot, 'package.json')));
  const result = [];
  for (const name of APP_CONFIG_FILES) result.push(...inputFile('app', appRoot, name));
  for (const name of OPTIONAL_APP_CONFIG_FILES)
    result.push(...inputFile('app', appRoot, name, true));
  // Request-time traces, test artifacts, private environment and emitted output
  // never enter source identity. Ordinary Next source is omitted only from the
  // development restart graph: the running verified compiler owns its HMR.
  result.push(
    ...prefixed(
      'app/scripts',
      subjectFiles(join(appRoot, 'scripts'), { extensions: /\.(?:[cm]?js|d\.mts)$/ }),
    ),
  );
  const src = development ? join(appRoot, 'src/server') : join(appRoot, 'src');
  result.push(
    ...prefixed(
      development ? 'app/src/server' : 'app/src',
      subjectFiles(src, {
        exclude: [/^(?:test|__tests__)(?:\/|$)/, /(?:^|\/)\.env(?:\.|$)/, /\.test\.[jt]sx?$/],
      }),
    ),
  );
  for (const [name, root] of [
    ['sdk', sdkRoot],
    ['ui', uiRoot],
    ['config', configRoot],
  ]) {
    if (!development || name !== 'ui')
      result.push(...prefixed(`${name}/src`, subjectFiles(join(root, 'src'))));
    result.push(
      ...prefixed(
        `${name}/scripts`,
        subjectFiles(join(root, 'scripts'), { extensions: /\.(?:[cm]?js|d\.mts)$/ }),
      ),
    );
    for (const file of ['package.json', 'tsconfig.json', 'tsconfig.native.json'])
      result.push(
        ...inputFile(
          name,
          root,
          file,
          file === 'tsconfig.native.json' || (name === 'config' && file === 'tsconfig.json'),
        ),
      );
    if (name === 'config') {
      for (const folder of ['tsconfig', 'tailwind', 'eslint'])
        result.push(...prefixed(`config/${folder}`, subjectFiles(join(root, folder))));
    }
  }
  for (const file of [
    'package.json',
    'pnpm-lock.yaml',
    'pnpm-workspace.yaml',
    '.npmrc',
    '.pnpmfile.cjs',
    '.nvmrc',
  ])
    result.push(...inputFile('workspace', frontendRoot, file));
  result.push(
    ...prefixed(
      'workspace/scripts',
      subjectFiles(join(frontendRoot, 'scripts'), { extensions: /\.(?:[cm]?js|d\.mts)$/ }),
    ),
  );
  result.push(...prefixed('workspace/framework', subjectFiles(join(frontendRoot, 'framework'))));
  result.push(...prefixed('workspace/patches', subjectFiles(join(frontendRoot, 'patches'))));
  return result.sort((a, b) => a.path.localeCompare(b.path, 'en'));
}
export function frameworkOutputGraph(identity) {
  const { appRoot } = identity;
  const next = join(appRoot, '.next');
  if (!existsSync(join(next, 'server')) || !existsSync(join(appRoot, '.server')))
    refuse('output_missing');
  const result = [
    ...prefixed('next/server', subjectFiles(join(next, 'server'))),
    ...prefixed('native', subjectFiles(join(appRoot, '.server'))),
    ...prefixed('sdk/dist', subjectFiles(join(packageRoot(appRoot, '@learnstack/sdk'), 'dist'))),
  ];
  for (const entry of readdirSync(next, { withFileTypes: true })) {
    if (
      entry.isDirectory() &&
      !['server', 'static', 'cache', 'types', 'diagnostics'].includes(entry.name)
    )
      refuse('output_directory');
    if (entry.isFile() && entry.name !== MANIFEST && CODE_EXTENSIONS.test(entry.name))
      result.push(...inputFile('next', next, entry.name));
    else if (entry.isSymbolicLink()) refuse('output_alias');
  }
  result.push(...inputFile('next', next, 'BUILD_ID'));
  return result.sort((a, b) => a.path.localeCompare(b.path, 'en'));
}
function sameGraph(actual, expected) {
  if (!Array.isArray(expected) || canonical(actual) !== canonical(expected))
    refuse('graph_identity');
}
function identityFields(identity) {
  return {
    format: identity.format,
    tuple: identity.tuple,
    censusSha256: identity.censusSha256,
    patchSha256: identity.patchSha256,
    generatorSha256: identity.generatorSha256,
  };
}
function assertLocalEnvironmentKeys(appRoot) {
  // Existing .env.local is a narrow runtime projection, not a compiler option
  // source. Inspect keys only; never retain, serialize, hash or log its values.
  // Repository-root private selection is not read by this guard.
  for (const name of [
    '.env',
    '.env.local',
    '.env.production',
    '.env.production.local',
    '.env.development',
    '.env.development.local',
    '.env.test',
    '.env.test.local',
  ]) {
    const path = join(appRoot, name);
    if (!existsSync(path)) continue;
    for (const line of regularBytes(path, 64 * 1024)
      .toString('utf8')
      .split(/\r?\n/)) {
      const text = line.trim();
      if (!text || text.startsWith('#')) continue;
      const match = /^(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=/.exec(text);
      if (!match || !LOCAL_RUNTIME_ENVIRONMENT.has(match[1])) refuse('local_environment_keys');
    }
  }
}
function assertBuildEnvironment(frontendRoot = FRAMEWORK_FRONTEND_ROOT) {
  if (process.env.BROWSERSLIST !== undefined && process.env.BROWSERSLIST !== QUALIFIED_BROWSERSLIST)
    refuse('build_environment');
  if (DISALLOWED_BUILD_ENVIRONMENT.some((key) => process.env[key])) refuse('build_environment');
  if (
    Object.keys(process.env).some(
      (key) =>
        /^(?:NEXT_|__NEXT_|SWC_|TURBOPACK|WEBPACK_|BROWSERSLIST|BABEL_|TS_NODE_|TS_JEST_|ESLINT_|POSTCSS_|TAILWIND_|IS_NEXT_WORKER|IS_TURBOPACK_TEST|EXPERIMENTAL_)/.test(
          key,
        ) && !OUTER_FRAMEWORK_ENVIRONMENT.has(key),
    )
  )
    refuse('build_environment');
  if (
    process.env.NODE_PATH &&
    process.env.NODE_PATH !== join(frontendRoot, 'node_modules/.pnpm/node_modules')
  )
    refuse('build_environment');
  // No NEXT_PUBLIC compile-time setting is declared. The closed prefix test
  // above refuses all such substitutions; no secret value/hash is serialized.
}
function producerOptions(mode) {
  return {
    mode,
    args: [],
    environment: {
      policy: 'closed-compiler-v1',
      NODE_ENV: mode,
      NODE_PATH: 'workspace/node_modules/.pnpm/node_modules',
      NEXT_TELEMETRY_DISABLED: '1',
      BROWSERSLIST: QUALIFIED_BROWSERSLIST,
      SWC_NATIVE_BINDING_CACHE: 'app/.framework-state/swc-native-cache',
    },
  };
}
function processEnvironment() {
  return Object.fromEntries(
    PROCESS_ENVIRONMENT_KEYS.filter((key) => process.env[key] !== undefined).map((key) => [
      key,
      process.env[key],
    ]),
  );
}
function producerEnvironment(identity, mode, extra = {}) {
  return {
    ...processEnvironment(),
    NODE_ENV: mode,
    NODE_PATH: join(identity.frontendRoot, 'node_modules/.pnpm/node_modules'),
    NEXT_TELEMETRY_DISABLED: '1',
    BROWSERSLIST: QUALIFIED_BROWSERSLIST,
    // next-intl's pinned @swc/core carrier materializes its attested native
    // payload here. A finite no-HOME fixture cannot fall back to a user cache;
    // this canonical location stays under the existing app output owner.
    SWC_NATIVE_BINDING_CACHE: join(identity.appRoot, '.framework-state/swc-native-cache'),
    ...extra,
  };
}
export function frameworkRuntimeEnvironment(identity, mode) {
  assertBuildEnvironment(identity.frontendRoot);
  const runtime = Object.fromEntries(
    [...LOCAL_RUNTIME_ENVIRONMENT]
      .filter((key) => process.env[key] !== undefined)
      .map((key) => [key, process.env[key]]),
  );
  return { ...producerEnvironment(identity, mode), ...runtime };
}
function producerManager(manager) {
  return {
    version: manager.version,
    sha256: manager.sha256,
    metadataSha256: manager.metadataSha256,
  };
}
function validProducerManager(identity, manager) {
  const metadata = managerFiles(identity.frontendRoot);
  return (
    manager?.version === identity.tuple.pnpm &&
    manager.sha256 === metadata.files.find((file) => file.path === 'bin/pnpm.cjs')?.sha256 &&
    manager.metadataSha256 ===
      sha256(regularBytes(join(identity.frontendRoot, 'framework/next-response-cleanup/pnpm.json')))
  );
}
export function verifyFrameworkBuild(options = {}) {
  const identity = verifyFrameworkInstallation(options);
  assertLocalEnvironmentKeys(identity.appRoot);
  assertBuildEnvironment(identity.frontendRoot);
  const began = performance.now();
  const manifest = jsonFile(join(identity.appRoot, '.next', MANIFEST), 16 * 1024 * 1024);
  if (
    manifest.format !== FRAMEWORK_FORMAT ||
    manifest.mode !== 'production' ||
    canonical(manifest.identity) !== canonical(identityFields(identity)) ||
    !validProducerManager(identity, manifest.producer?.pnpm) ||
    manifest.producer?.node !== process.versions.node ||
    canonical(manifest.buildOptions) !== canonical(producerOptions('production'))
  )
    refuse('build_manifest');
  sameGraph(frameworkInputGraph(identity), manifest.inputs);
  const outputs = frameworkOutputGraph(identity);
  sameGraph(outputs, manifest.outputs);
  return Object.freeze({
    identity,
    manifest,
    generated: Object.freeze({
      files: outputs.length,
      bytes: outputs.reduce((sum, file) => sum + file.bytes, 0),
      milliseconds: performance.now() - began,
    }),
  });
}
function safeOutput(root, name) {
  const path = join(root, name);
  if (existsSync(path)) {
    if (
      lstatSync(path).isSymbolicLink() ||
      realpathSync(path) !== path ||
      !statSync(path).isDirectory()
    )
      refuse('output_ownership');
  }
  return path;
}
function ownerState(root) {
  const state = safeOutput(root, '.framework-state');
  if (!existsSync(state)) {
    mkdirSync(state, { mode: 0o700 });
    writeFileSync(join(state, 'format.json'), '{"format":1}\n', { mode: 0o600, flag: 'wx' });
    mkdirSync(join(state, 'readers'), { mode: 0o700 });
  }
  if (
    canonical(jsonFile(join(state, 'format.json'), 4096)) !== '{"format":1}' ||
    !lstatSync(join(state, 'readers')).isDirectory()
  )
    refuse('owner_state');
  return state;
}
function resourceLease(root, record, shared) {
  const state = ownerState(root);
  const gate = join(state, 'gate');
  function gated(action) {
    const began = performance.now();
    for (;;) {
      try {
        mkdirSync(gate, { mode: 0o700 });
        break;
      } catch {
        // This directory serializes metadata access; a compatible reader is not
        // an ownership conflict. Wait for actual mkdir success within the same
        // existing bound used by retirement. Owner conflicts below still refuse
        // immediately after the metadata gate is held.
        if (performance.now() - began >= 250) refuse('owner_busy');
        Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 1);
      }
    }
    try {
      return action();
    } finally {
      rmdirSync(gate);
    }
  }
  const path = shared ? join(state, 'readers', record.nonce) : join(state, 'writer.json');
  gated(() => {
    if (
      existsSync(join(state, 'writer.json')) ||
      (!shared && readdirSync(join(state, 'readers')).length)
    )
      refuse('owner_busy');
    writeFileSync(path, `${canonical(record)}\n`, { mode: 0o600, flag: 'wx' });
  });
  let released = false;
  return () => {
    if (released) return;
    gated(() => {
      if (canonical(jsonFile(path, 4096)) !== canonical(record)) refuse('owner_changed');
      unlinkSync(path);
    });
    released = true;
  };
}
export function acquireFrameworkLease({
  appRoot = process.cwd(),
  frontendRoot = FRAMEWORK_FRONTEND_ROOT,
  kind = 'build',
  ownerPid = process.pid,
} = {}) {
  appRoot = checkedRoot(appRoot);
  frontendRoot = checkedRoot(frontendRoot);
  if (!['build', 'native', 'production', 'development', 'install'].includes(kind))
    refuse('owner_kind');
  if (
    !Number.isSafeInteger(ownerPid) ||
    ownerPid <= 0 ||
    (kind !== 'install' && ownerPid !== process.pid)
  )
    refuse('owner_pid');
  const record = {
    format: FRAMEWORK_FORMAT,
    pid: ownerPid,
    nonce: randomBytes(16).toString('hex'),
    kind,
  };
  const releases = [];
  const release = () => {
    const errors = [];
    for (const retire of releases.toReversed()) {
      try {
        retire();
      } catch (error) {
        errors.push(error);
      }
    }
    if (errors.length) throw new AggregateError(errors, 'Framework ownership cleanup failed');
  };
  try {
    releases.push(resourceLease(frontendRoot, record, kind !== 'install'));
    if (kind !== 'install') {
      releases.push(resourceLease(appRoot, record, false));
      releases.push(
        resourceLease(packageRoot(appRoot, '@learnstack/sdk'), record, kind === 'production'),
      );
    }
    return { record: Object.freeze(record), release };
  } catch (error) {
    release();
    throw error;
  }
}
const ROUTES_STUB =
  '// Auto-generated stub by scripts/ensure-routes-stub.mjs.\n' +
  '// Replaced by `next dev` / `next build` with real typed-routes definitions.\n' +
  'export {};\n';
function emptyOrRoutesStub(path, name) {
  if (!existsSync(path)) return true;
  const files = distributionPaths(path);
  if (!files.length) return true;
  return (
    name === '.next' &&
    canonical(files) === canonical(['types/routes.d.ts']) &&
    regularBytes(join(path, 'types/routes.d.ts'), 4096).toString('utf8') === ROUTES_STUB
  );
}
function claimOwnedOutput(root, name) {
  if (!['.next', '.server', 'dist'].includes(name)) refuse('output_name');
  const output = safeOutput(root, name);
  const state = ownerState(root);
  const recordPath = join(state, `output-${name.replaceAll('.', '')}.json`);
  if (existsSync(recordPath)) {
    const record = jsonFile(recordPath, 4096);
    if (
      record.format !== 1 ||
      record.root !== root ||
      record.path !== name ||
      !/^[0-9a-f]{32}$/.test(record.nonce)
    )
      refuse('output_ownership');
  } else {
    // A lease controls future writers. It cannot retroactively establish who
    // produced existing executable output. Only absent/empty roots or the exact
    // non-executable fresh-install routes stub can be adopted automatically.
    if (!emptyOrRoutesStub(output, name)) refuse('output_unowned');
    writeFileSync(
      recordPath,
      `${canonical({ format: 1, root, path: name, nonce: randomBytes(16).toString('hex') })}\n`,
      { mode: 0o600, flag: 'wx' },
    );
  }
  // Do not recursively remove through any alias, even inside an owned root.
  if (existsSync(output)) distributionPaths(output);
  return output;
}
function assertMutatingOwner(identity, owner, nativeOnly) {
  if (
    !owner ||
    owner.pid !== process.pid ||
    (nativeOnly ? owner.kind !== 'native' : !['build', 'development'].includes(owner.kind))
  )
    refuse('mutation_owner');
  const records = [
    join(identity.frontendRoot, '.framework-state/readers', owner.nonce),
    join(identity.appRoot, '.framework-state/writer.json'),
    join(packageRoot(identity.appRoot, '@learnstack/sdk'), '.framework-state/writer.json'),
  ];
  for (const path of records)
    if (canonical(jsonFile(path, 4096)) !== canonical(owner)) refuse('mutation_owner');
}
export function cleanFrameworkOutputs(identity, { nativeOnly = false, owner } = {}) {
  assertMutatingOwner(identity, owner, nativeOnly);
  const outputs = (nativeOnly ? ['.server'] : ['.next', '.server']).map((name) =>
    claimOwnedOutput(identity.appRoot, name),
  );
  outputs.push(claimOwnedOutput(packageRoot(identity.appRoot, '@learnstack/sdk'), 'dist'));
  // Establish every root's authority before deleting any current output.
  for (const output of outputs) {
    rmSync(output, { recursive: true, force: true });
    mkdirSync(output, { recursive: true, mode: 0o700 });
  }
}
export function prepareFrameworkNativeCache(identity, owner) {
  assertFrameworkProcessFamilySupport();
  // This is vendor-attested native payload materialization, not generated Next
  // output. Keep it under the existing app owner; never adopt populated unknown
  // caches, follow aliases, or clear a directory merely to make loading work.
  if (
    !owner ||
    owner.pid !== process.pid ||
    !['native', 'build', 'development', 'production'].includes(owner.kind)
  )
    refuse('native_cache_owner');
  for (const path of [
    join(identity.appRoot, '.framework-state/writer.json'),
    join(identity.frontendRoot, '.framework-state/readers', owner.nonce),
  ])
    if (canonical(jsonFile(path, 4096)) !== canonical(owner)) refuse('native_cache_owner');
  const state = safeOutput(identity.appRoot, '.framework-state');
  const cache = safeOutput(state, 'swc-native-cache');
  const recordPath = join(state, 'native-cache.json');
  const present = existsSync(recordPath);
  if (present) {
    const record = jsonFile(recordPath, 4096);
    if (
      record.format !== 1 ||
      record.root !== identity.appRoot ||
      record.path !== '.framework-state/swc-native-cache' ||
      !/^[0-9a-f]{32}$/.test(record.nonce)
    )
      refuse('native_cache_ownership');
  }
  if (existsSync(cache)) {
    const actual = lstatSync(cache);
    if (
      actual.uid !== process.getuid() ||
      (actual.mode & 0o777) !== 0o700 ||
      (!present && readdirSync(cache).length)
    )
      refuse('native_cache_ownership');
    distributionPaths(cache); // Refuse every nested alias before vendor code loads.
  }
  if (!present)
    writeFileSync(
      recordPath,
      `${canonical({ format: 1, root: identity.appRoot, path: '.framework-state/swc-native-cache', nonce: randomBytes(16).toString('hex') })}\n`,
      { mode: 0o600, flag: 'wx' },
    );
  if (!existsSync(cache)) mkdirSync(cache, { mode: 0o700 });
  return cache;
}
async function runOwnedNode(script, args, { cwd, environment } = {}) {
  try {
    await runFrameworkProcessFamily(process.execPath, [script, ...args], { cwd, environment });
  } catch {
    refuse('producer_failed');
  }
}

async function compileFrameworkNative(identity, producer, mode) {
  const sdkRoot = packageRoot(identity.appRoot, '@learnstack/sdk');
  const require = createRequire(join(identity.appRoot, 'package.json'));
  await runOwnedNode(join(sdkRoot, 'scripts/build-native.mjs'), [], {
    cwd: sdkRoot,
    environment: producerEnvironment(identity, mode, {
      LEARNSTACK_FRAMEWORK_PRODUCER_OWNER: producer.nonce,
    }),
  });
  await runOwnedNode(require.resolve('typescript/bin/tsc'), ['--project', 'tsconfig.server.json'], {
    cwd: identity.appRoot,
    environment: producerEnvironment(identity, mode),
  });
}
function finishFrameworkBuild(identity, manager, before) {
  const after = verifyFrameworkInstallation(identity);
  assertLocalEnvironmentKeys(identity.appRoot);
  if (canonical(identityFields(after)) !== canonical(identityFields(identity)))
    refuse('producer_identity');
  const inputs = frameworkInputGraph(after);
  sameGraph(inputs, before);
  const outputs = frameworkOutputGraph(after);
  const manifest = {
    format: FRAMEWORK_FORMAT,
    mode: 'production',
    identity: identityFields(after),
    producer: { node: process.versions.node, pnpm: producerManager(manager) },
    buildOptions: producerOptions('production'),
    inputs,
    outputs,
  };
  const path = join(identity.appRoot, '.next', MANIFEST);
  const temporary = `${path}.${randomBytes(8).toString('hex')}.tmp`;
  writeFileSync(temporary, `${JSON.stringify(manifest, null, 2)}\n`, { mode: 0o600, flag: 'wx' });
  renameSync(temporary, path);
  return manifest;
}
export function prepareFrameworkBuild(options = {}) {
  assertFrameworkProcessFamilySupport();
  assertBuildEnvironment();
  const identity = verifyFrameworkInstallation(options);
  assertLocalEnvironmentKeys(identity.appRoot);
  const manager = resolveQualifiedPnpm(identity);
  return { identity, manager };
}

function initializeFrameworkDevelopment(identity, manager) {
  for (const name of readdirSync(join(identity.appRoot, '.framework-state'))) {
    if (/^compiler-(?:client|server|edge)\.json$/.test(name))
      unlinkSync(join(identity.appRoot, '.framework-state', name));
  }
  const previous = join(identity.appRoot, DEVELOPMENT);
  if (existsSync(previous)) unlinkSync(previous);
  const manifest = {
    format: FRAMEWORK_FORMAT,
    mode: 'development',
    identity: identityFields(identity),
    producer: { node: process.versions.node, pnpm: producerManager(manager) },
    resolutions: Object.fromEntries(
      ['next', '@learnstack/sdk', '@learnstack/ui', '@learnstack/config'].map((name) => [
        name,
        packageRoot(identity.appRoot, name),
      ]),
    ),
    inputs: frameworkInputGraph(identity, { development: true }),
  };
  writeFileSync(join(identity.appRoot, DEVELOPMENT), `${JSON.stringify(manifest, null, 2)}\n`, {
    mode: 0o600,
    flag: 'wx',
  });
  return manifest;
}
export function verifyFrameworkDevelopment(options = {}) {
  // Next legitimately adds its own private loader flags after bootstrap. The
  // qualified initial environment is finite; HMR rechecks the effective target
  // itself rather than rejecting framework-internal flags created afterwards.
  if (process.env.BROWSERSLIST !== QUALIFIED_BROWSERSLIST) refuse('build_environment');
  const identity = verifyFrameworkInstallation(options);
  assertLocalEnvironmentKeys(identity.appRoot);
  const manifest = jsonFile(join(identity.appRoot, DEVELOPMENT), 16 * 1024 * 1024);
  if (
    manifest.format !== FRAMEWORK_FORMAT ||
    manifest.mode !== 'development' ||
    canonical(manifest.identity) !== canonical(identityFields(identity)) ||
    manifest.producer?.pnpm?.version !== identity.tuple.pnpm
  )
    refuse('development_identity');
  if (
    canonical(manifest.resolutions) !==
    canonical(
      Object.fromEntries(
        ['next', '@learnstack/sdk', '@learnstack/ui', '@learnstack/config'].map((name) => [
          name,
          packageRoot(identity.appRoot, name),
        ]),
      ),
    )
  )
    refuse('development_resolution');
  sameGraph(frameworkInputGraph(identity, { development: true }), manifest.inputs);
  return identity;
}

// The native response fence reads only restart-critical project inputs. Actual
// framework subjects and the broader producer graph are verified per compiler,
// not scanned per HTTP request. Dependencies remain owned/immutable meanwhile.
export function frameworkDevelopmentRestartGraph(identity) {
  assertLocalEnvironmentKeys(identity.appRoot);
  assertQualifiedConfiguration(identity.appRoot, jsonFile(join(identity.appRoot, 'package.json')));
  const result = [];
  for (const file of APP_CONFIG_FILES) result.push(...inputFile('app', identity.appRoot, file));
  for (const file of OPTIONAL_APP_CONFIG_FILES)
    result.push(...inputFile('app', identity.appRoot, file, true));
  result.push(...prefixed('app/src/server', subjectFiles(join(identity.appRoot, 'src/server'))));
  const sdk = packageRoot(identity.appRoot, '@learnstack/sdk');
  result.push(...prefixed('sdk/src', subjectFiles(join(sdk, 'src'))));
  for (const file of ['package.json', 'pnpm-lock.yaml', '.npmrc'])
    result.push(...inputFile('workspace', identity.frontendRoot, file));
  return result.sort((a, b) => a.path.localeCompare(b.path, 'en'));
}

// This is the only manifest producer. It always claims/cleans output and awaits
// the actual qualified compiler; no exported API signs an existing directory.
export async function produceFrameworkBuild({
  appRoot = process.cwd(),
  frontendRoot,
  mode = 'production',
  nativeOnly = false,
} = {}) {
  if (!['production', 'development'].includes(mode)) refuse('producer_mode');
  const { identity, manager } = prepareFrameworkBuild({ appRoot, frontendRoot });
  const lease = acquireFrameworkLease({
    ...identity,
    kind: nativeOnly ? 'native' : mode === 'development' ? 'development' : 'build',
  });
  let failed = false;
  try {
    prepareFrameworkNativeCache(identity, lease.record);
    cleanFrameworkOutputs(identity, { nativeOnly, owner: lease.record });
    await runOwnedNode(join(identity.appRoot, 'scripts/ensure-routes-stub.mjs'), [], {
      cwd: identity.appRoot,
      environment: producerEnvironment(identity, mode),
    });
    const inputs = frameworkInputGraph(identity);
    await compileFrameworkNative(identity, lease.record, mode);
    const compiledIdentity = verifyFrameworkInstallation(identity);
    sameGraph(frameworkInputGraph(compiledIdentity), inputs);
    if (nativeOnly) return { identity, manager };
    if (mode === 'development') {
      initializeFrameworkDevelopment(identity, manager);
      return { identity, manager, lease };
    }
    await runOwnedNode(join(identity.nextRoot, 'dist/bin/next'), ['build'], {
      cwd: identity.appRoot,
      environment: producerEnvironment(identity, mode),
    });
    const manifest = finishFrameworkBuild(identity, manager, inputs);
    return { identity, manager, manifest };
  } catch (error) {
    failed = true;
    throw error;
  } finally {
    if (mode !== 'development' || nativeOnly || failed) {
      try {
        lease.release();
      } catch (error) {
        if (!failed) throw error;
        console.error('Framework producer ownership cleanup failed');
      }
    }
  }
}

// Closed test-fixture transfer: preserve the original completed manifest. It
// never signs/relabels output, and only copies into absent/empty target roots.
export function copyQualifiedFrameworkBuild({
  sourceAppRoot,
  targetAppRoot,
  frontendRoot = FRAMEWORK_FRONTEND_ROOT,
}) {
  sourceAppRoot = checkedRoot(sourceAppRoot);
  targetAppRoot = checkedRoot(targetAppRoot);
  if (sourceAppRoot === targetAppRoot) refuse('copy_resolution');
  const sourceSDK = packageRoot(sourceAppRoot, '@learnstack/sdk');
  const targetSDK = packageRoot(targetAppRoot, '@learnstack/sdk');
  const sourceLease = acquireFrameworkLease({
    appRoot: sourceAppRoot,
    frontendRoot,
    kind: 'production',
  });
  let targetLease;
  let failed = false;
  try {
    // A shared SDK is read, never upgraded to a writer beneath our read lease.
    // An isolated fresh SDK gets its own exclusive output owner for the copy.
    targetLease = acquireFrameworkLease({
      appRoot: targetAppRoot,
      frontendRoot,
      kind: sourceSDK === targetSDK ? 'production' : 'build',
    });
    const qualified = verifyFrameworkBuild({ appRoot: sourceAppRoot, frontendRoot });
    const targetIdentity = verifyFrameworkInstallation({ appRoot: targetAppRoot, frontendRoot });
    sameGraph(frameworkInputGraph(targetIdentity), qualified.manifest.inputs);
    const roots = [
      { source: sourceAppRoot, target: targetAppRoot, name: '.next' },
      { source: sourceAppRoot, target: targetAppRoot, name: '.server' },
      ...(sourceSDK === targetSDK ? [] : [{ source: sourceSDK, target: targetSDK, name: 'dist' }]),
    ];
    for (const root of roots) {
      if (!emptyOrRoutesStub(safeOutput(root.target, root.name), root.name))
        refuse('copy_target_not_empty');
    }
    const outputs = roots.map((root) => ({
      ...root,
      output: claimOwnedOutput(root.target, root.name),
    }));
    for (const { source, name, output } of outputs) {
      rmSync(output, { recursive: true, force: true });
      cpSync(join(source, name), output, {
        recursive: true,
        dereference: false,
        errorOnExist: true,
        filter(path) {
          const part = relative(join(source, name), path).replaceAll('\\', '/');
          if (
            name === '.next' &&
            (part === 'cache' || part.startsWith('cache/') || part === 'trace')
          )
            return false;
          if (lstatSync(path).isSymbolicLink()) refuse('copy_alias');
          return true;
        },
      });
    }
    verifyFrameworkBuild({ appRoot: sourceAppRoot, frontendRoot });
    return verifyFrameworkBuild({ appRoot: targetAppRoot, frontendRoot });
  } catch (error) {
    failed = true;
    throw error;
  } finally {
    const errors = [];
    for (const lease of [targetLease, sourceLease]) {
      try {
        lease?.release();
      } catch (error) {
        errors.push(error);
      }
    }
    if (errors.length) {
      if (!failed) throw new AggregateError(errors, 'Framework copy ownership cleanup failed');
      console.error('Framework copy ownership cleanup failed');
    }
  }
}

export function assertFrameworkSDKProducer(sdkRoot) {
  sdkRoot = checkedRoot(sdkRoot);
  censusIdentity(FRAMEWORK_FRONTEND_ROOT); // exact actual Node before compiler work
  const token = process.env.LEARNSTACK_FRAMEWORK_PRODUCER_OWNER;
  const owner = jsonFile(join(sdkRoot, '.framework-state/writer.json'), 4096);
  if (
    !token ||
    owner.format !== 1 ||
    owner.nonce !== token ||
    owner.pid !== process.ppid ||
    !['build', 'native', 'development'].includes(owner.kind)
  )
    refuse('sdk_producer');
}

// Called synchronously by pnpm's project hook before package-tree mutation.
// Lifecycle hooks still check the child CLI and final subjects, but are too late
// to establish the pre-mutation ownership boundary by themselves.
export function assertFrameworkInstallOwner() {
  const root = FRAMEWORK_FRONTEND_ROOT;
  const token = process.env.LEARNSTACK_FRAMEWORK_INSTALL_OWNER;
  const record = jsonFile(join(root, '.framework-state/writer.json'), 4096);
  if (
    !token ||
    record.format !== 1 ||
    record.kind !== 'install' ||
    record.nonce !== token ||
    record.pid !== process.ppid
  )
    refuse('install_owner');
  resolveQualifiedPnpm({ frontendRoot: root, executable: process.argv[1], lifecycle: true });
}
