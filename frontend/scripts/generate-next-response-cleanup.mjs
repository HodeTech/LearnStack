// Regenerate only from the original locked distribution, never from a patched
// store. Output is an owned private directory; installation remains pnpm's job.
import { createHash } from 'node:crypto';
import { readFile, writeFile, mkdir, lstat, readdir } from 'node:fs/promises';
import { resolve, join, dirname, basename } from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { decode } from '@jridgewell/sourcemap-codec';
import { editMap, startsOf, position } from './next-response-patch/map-editor.mjs';
import { transformJavascript, parseJavascript } from './next-response-patch/transform.mjs';

const execute = promisify(execFile);
const frontendRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const framework = 'framework/next-response-cleanup';
const sha = (bytes) => createHash('sha256').update(bytes).digest('hex');
function invariant(condition, message) {
  if (!condition) throw new Error(`next patch: ${message}`);
}

async function regularBytes(path, maxBytes = 12 * 1024 * 1024) {
  const stat = await lstat(path);
  invariant(
    stat.isFile() && !stat.isSymbolicLink() && stat.size <= maxBytes,
    `unqualified input: ${path}`,
  );
  return readFile(path);
}

function patchHash(text) {
  const bytes = createHash('md5').update(text.replaceAll('\r\n', '\n')).digest();
  const alphabet = 'abcdefghijklmnopqrstuvwxyz234567';
  let bits = 0,
    value = 0,
    result = '';
  for (const byte of bytes) {
    value = (value << 8) | byte;
    bits += 8;
    while (bits >= 5) {
      result += alphabet[(value >>> (bits - 5)) & 31];
      bits -= 5;
    }
  }
  if (bits) result += alphabet[(value << (5 - bits)) & 31];
  return result;
}

async function writeSubject(root, path, bytes) {
  const destination = join(root, path);
  await mkdir(dirname(destination), { recursive: true });
  await writeFile(destination, bytes, { flag: 'wx' });
}

function assertMapAttribution(code, map, marker, sourceSuffix) {
  const offset = code.indexOf(marker);
  invariant(offset >= 0 && code.indexOf(marker, offset + 1) < 0, `nonunique ${marker}`);
  const [line, column] = position(startsOf(code), offset);
  let matching;
  for (const segment of decode(map.mappings)[line] ?? []) {
    if (segment[0] > column) break;
    matching = segment;
  }
  invariant(
    matching?.length >= 4 && map.sources[matching[1]].endsWith(sourceSuffix),
    `unexpected source map attribution for ${marker}`,
  );
}

async function scanDistribution(root) {
  const found = [];
  const paths = [];
  async function scan(directory, prefix) {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      const name = prefix ? `${prefix}/${entry.name}` : entry.name;
      invariant(!entry.isSymbolicLink(), `distribution symlink: ${name}`);
      if (entry.isDirectory()) await scan(join(directory, entry.name), name);
      else if (entry.isFile()) {
        paths.push(name);
        if (entry.name.endsWith('.js')) {
          const code = await readFile(join(directory, entry.name), 'utf8');
          if (code.includes('failed to pipe response')) found.push(name);
        }
      }
    }
  }
  await scan(root, '');
  return { pipes: found.sort(), paths: paths.sort() };
}

export async function generateNextResponseCleanup({ packageRoot, outputRoot }) {
  invariant(process.versions.node === '22.23.1', 'unqualified Node');
  const require = createRequire(import.meta.url);
  const codecRoot = resolve(dirname(require.resolve('@jridgewell/sourcemap-codec')), '..');
  invariant(
    JSON.parse(await regularBytes(join(codecRoot, 'package.json'))).version === '1.5.5',
    'unqualified source-map codec',
  );
  packageRoot = resolve(packageRoot);
  outputRoot = resolve(outputRoot);
  // mkdir without recursive deliberately refuses reuse/overwrite of old output.
  await mkdir(outputRoot);
  const originalBytes = await regularBytes(join(frontendRoot, framework, 'originals.json'));
  const originals = JSON.parse(originalBytes);
  invariant(
    originals.schemaVersion === 1 && originals.next === '15.5.28' && originals.files.length === 59,
    'unexpected original census',
  );
  const packageBytes = await regularBytes(join(packageRoot, 'package.json'));
  const pkg = JSON.parse(packageBytes);
  const packageMetadata = originals.packageMetadata;
  invariant(
    packageMetadata?.path === 'package.json' &&
      packageMetadata.runtimeEntry === 'dist/server/next.js' &&
      packageMetadata.compilerEntry === 'dist/bin/next' &&
      packageBytes.length === packageMetadata.bytes &&
      sha(packageBytes) === packageMetadata.sha256 &&
      pkg.name === 'next' &&
      pkg.version === originals.next &&
      pkg.main === `./${packageMetadata.runtimeEntry}` &&
      pkg.bin?.next === `./${packageMetadata.compilerEntry}` &&
      pkg.exports === undefined,
    'unqualified Next package metadata or entry',
  );
  const ownerSource = (await regularBytes(join(frontendRoot, framework, 'owner.mjs'))).toString(
    'utf8',
  );
  const jsSubjects = originals.files.filter((f) => f.path.endsWith('.js'));
  const expectedPipes = jsSubjects
    .filter(
      (f) =>
        f.path.includes('/pipe-readable.js') || f.path.startsWith('dist/compiled/next-server/'),
    )
    .map((f) => f.path)
    .sort();
  invariant(jsSubjects.length === 30 && expectedPipes.length === 27, 'unexpected file families');
  const actualDistribution = await scanDistribution(packageRoot);
  const actualPipes = actualDistribution.pipes;
  invariant(
    JSON.stringify(actualPipes) === JSON.stringify(expectedPipes),
    'pipe distribution census drift',
  );
  invariant(
    JSON.stringify(actualDistribution.paths) === JSON.stringify(originals.distribution?.paths),
    'original distribution membership drift',
  );
  const input = new Map();
  for (const fact of originals.files) {
    invariant(
      /^dist\/(?:[a-zA-Z0-9._-]+\/)*[a-zA-Z0-9._-]+$/.test(fact.path),
      'unsafe subject path',
    );
    const bytes = await regularBytes(join(packageRoot, fact.path));
    invariant(
      bytes.length === fact.bytes && sha(bytes) === fact.sha256,
      `original mismatch: ${fact.path}`,
    );
    input.set(fact.path, bytes);
    await writeSubject(join(outputRoot, 'original'), fact.path, bytes);
  }
  const files = [],
    absentPaths = [],
    javascriptRestoration = [];
  for (const fact of jsSubjects) {
    const code = input.get(fact.path).toString('utf8');
    const kind = expectedPipes.includes(fact.path)
      ? 'pipe'
      : fact.path === 'dist/compiled/compression/index.js'
        ? 'compression'
        : 'router';
    const edits = transformJavascript({ code, filename: fact.path, kind, ownerSource });
    let output, mapResult;
    if (kind !== 'compression') {
      const oldMapPath = `${fact.path}.map`;
      const oldMap = JSON.parse(input.get(oldMapPath).toString('utf8'));
      if (kind === 'pipe') {
        assertMapAttribution(code, oldMap, 'failed to pipe response', '/pipe-readable.ts');
        assertMapAttribution(
          code,
          oldMap,
          'failed to write chunk to response',
          '/pipe-readable.ts',
        );
      } else
        assertMapAttribution(
          code,
          oldMap,
          'let requestHandler = requestHandlerImpl',
          '/router-server.ts',
        );
      const directive = /(?:\/\/#[ \t]*sourceMappingURL=)([^\r\n]+)(\r?\n)?$/.exec(code);
      invariant(
        directive && directive[1] === basename(oldMapPath),
        'unexpected trailing map directive',
      );
      const start = directive.index + directive[0].indexOf(directive[1]);
      edits.push({
        start,
        end: start + directive[1].length,
        text: `${basename(fact.path)}.learnstack.map`,
        source: 'learnstack-next-cleanup/map-url.js',
      });
      mapResult = editMap({ code, map: oldMap, edits });
      output = mapResult.code;
      const mapPath = `${fact.path}.learnstack.map`;
      const bytes = Buffer.from(JSON.stringify(mapResult.map));
      const originalMap = originals.files.find((f) => f.path === oldMapPath);
      files.push({
        kind: 'map',
        originalPath: oldMapPath,
        originalSha256: originalMap.sha256,
        originalBytes: originalMap.bytes,
        path: mapPath,
        sha256: sha(bytes),
        bytes: bytes.length,
      });
      absentPaths.push(oldMapPath);
      await writeSubject(join(outputRoot, 'patched'), mapPath, bytes);
    } else {
      // No fictitious source map is introduced for the bundled compression code.
      const sorted = edits.slice().sort((a, b) => a.start - b.start);
      let cursor = 0;
      output = '';
      for (const e of sorted) {
        invariant(e.start >= cursor, 'overlapping compression edits');
        output += code.slice(cursor, e.start);
        e.newStart = output.length;
        output += e.text;
        e.newEnd = output.length;
        cursor = e.end;
      }
      output += code.slice(cursor);
      mapResult = { edits: sorted };
    }
    parseJavascript(output, fact.path);
    const bytes = Buffer.from(output);
    files.push({
      kind,
      originalPath: fact.path,
      originalSha256: fact.sha256,
      originalBytes: fact.bytes,
      path: fact.path,
      sha256: sha(bytes),
      bytes: bytes.length,
      ...(kind === 'compression' ? { mapPath: null } : { mapPath: `${fact.path}.learnstack.map` }),
    });
    javascriptRestoration.push({
      path: fact.path,
      originalSha256: fact.sha256,
      edits: mapResult.edits.map((e) => ({
        start: e.newStart,
        end: e.newEnd,
        text: code.slice(e.start, e.end),
      })),
    });
    await writeSubject(join(outputRoot, 'patched'), fact.path, bytes);
  }
  const git = [
    '-c',
    'core.excludesFile=/tmp/learnstack-p02b7-safe-excludes',
    '-c',
    'diff.renames=false',
    'diff',
    '--no-index',
    '--no-renames',
    '--no-ext-diff',
    '--no-textconv',
    '--no-color',
    '--diff-algorithm=myers',
    '--no-indent-heuristic',
    '--src-prefix=a/',
    '--dst-prefix=b/',
    '--irreversible-delete',
    '--full-index',
    '--text',
    '--',
    'original',
    'patched',
  ];
  let rawPatch;
  try {
    await execute('git', git, { cwd: outputRoot, maxBuffer: 180 * 1024 * 1024 });
    throw new Error('expected a nonempty patch');
  } catch (error) {
    invariant(error.code === 1 && typeof error.stdout === 'string', 'patch producer failed');
    rawPatch = error.stdout;
  }
  // Only header paths are normalized. A source line that happens to contain
  // one of these strings must remain byte-identical.
  let effectCount = 0;
  const normalized = rawPatch
    .split('\n')
    .map((line) => {
      if (line.startsWith('diff --git ')) {
        const match =
          /^diff --git a\/(?:original|patched)\/(dist\/[^ ]+) b\/(?:original|patched)\/(dist\/[^ ]+)$/.exec(
            line,
          );
        invariant(match && match[1] === match[2], 'unexpected diff effect');
        effectCount++;
        return `diff --git a/${match[1]} b/${match[2]}`;
      }
      if (line.startsWith('--- a/original/')) return line.replace('--- a/original/', '--- a/');
      if (line.startsWith('+++ b/patched/')) return line.replace('+++ b/patched/', '+++ b/');
      return line;
    })
    .join('\n');
  invariant(effectCount === 88, 'unexpected patch path count');
  invariant(
    Buffer.byteLength(normalized) < 90 * 1024 * 1024,
    'patch lacks repository size headroom',
  );
  const generatorInputs = [
    'scripts/generate-next-response-cleanup.mjs',
    'scripts/next-response-patch/transform.mjs',
    'scripts/next-response-patch/map-editor.mjs',
    `${framework}/owner.mjs`,
    `${framework}/originals.json`,
    `${framework}/pnpm.json`,
  ];
  const inputs = [];
  for (const path of generatorInputs)
    inputs.push({ path, sha256: sha(await regularBytes(join(frontendRoot, path))) });
  const census = {
    schemaVersion: 1,
    tuple: { node: '22.23.1', pnpm: '9.12.3', next: '15.5.28' },
    manager: JSON.parse(await regularBytes(join(frontendRoot, framework, 'pnpm.json'))),
    packageMetadata,
    generator: {
      path: generatorInputs[0],
      sha256: inputs[0].sha256,
      version: 1,
      tools: { typescript: '5.6.3', sourcemapCodec: '1.5.5' },
      inputs,
    },
    patch: {
      path: 'patches/next@15.5.28.patch',
      sha256: sha(normalized),
      pnpmHash: patchHash(normalized),
    },
    files: files.sort((a, b) => a.path.localeCompare(b.path)),
    absentPaths: absentPaths.sort(),
    javascriptRestoration,
    distribution: {
      paths: actualDistribution.paths
        .filter((path) => !absentPaths.includes(path))
        .concat(absentPaths.map((path) => path.slice(0, -4) + '.learnstack.map'))
        .sort(),
    },
  };
  await writeFile(join(outputRoot, 'next@15.5.28.patch'), normalized, { flag: 'wx' });
  await writeFile(join(outputRoot, 'census.json'), JSON.stringify(census, null, 2) + '\n', {
    flag: 'wx',
  });
  return census;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  invariant(
    args.length === 4 && args[0] === '--package-root' && args[2] === '--output-root',
    'usage: --package-root PATH --output-root NEW_DIRECTORY',
  );
  const result = await generateNextResponseCleanup({ packageRoot: args[1], outputRoot: args[3] });
  console.log(JSON.stringify({ files: result.files.length, patchSha256: result.patch.sha256 }));
}
