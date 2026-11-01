// Private shape controls only: no Next execution or producer build.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, mkdtempSync, writeFileSync, rmSync, realpathSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import {
  instrumentCopiedBffRuntime,
  instrumentBffProofRequestRuntime,
} from '../apps/web/scripts/bff-proof-host.mjs';
import { instrumentCopiedTransport } from '../apps/web/scripts/framework-transport-proof-source.mjs';
import { verifyFrameworkInstallation } from './framework-installation.mjs';
import { instrumentCopiedOverlap } from '../apps/web/scripts/framework-overlap-proof-source.mjs';
const repo = fileURLToPath(new URL('../../', import.meta.url));
const require = createRequire(new URL('../apps/web/package.json', import.meta.url));
const ts = require('typescript');
const read = (path) => readFileSync(join(repo, path), 'utf8');
function inputs() {
  const native = read('frontend/apps/web/scripts/public-server-runtime.mjs');
  const runtime = instrumentCopiedBffRuntime(native, {
    uiMode: true,
    isolatedProtocol: true,
    external: undefined,
    mutation: undefined,
    fakeIssuer: 'https://identity.example.test',
    fakeLogout: 'https://identity.example.test/logout',
    sites: [{ host: 'institution.example.test:3000', displayName: 'Institution', locale: 'en' }],
    marker: 'fixture-marker',
    clockBase: 1800000000000,
  });
  return {
    runtime,
    requestRuntime: instrumentBffProofRequestRuntime(
      read('frontend/apps/web/src/server/auth/request-runtime.ts'),
    ),
    snapshotPool: read('frontend/apps/web/src/server/auth/page-snapshot-pool.ts'),
    sessionStore: read('frontend/apps/web/src/server/auth/session-store.ts'),
    page: read('frontend/apps/web/src/app/(studio)/studio/page.tsx'),
    observer: read('frontend/apps/web/scripts/framework-proof-transport-observer.mjs'),
  };
}
const moduleText = readFileSync(
  new URL('../apps/web/scripts/framework-overlap-proof-source.mjs', import.meta.url),
  'utf8',
);
const syntax = ts.createSourceFile(
  'overlap-source.mjs',
  moduleText,
  ts.ScriptTarget.Latest,
  true,
  ts.ScriptKind.JS,
);
const anchors = [];
function visit(node) {
  if (
    ts.isCallExpression(node) &&
    ts.isIdentifier(node.expression) &&
    node.expression.text === 'replaceOnce'
  ) {
    const [input, needle] = node.arguments;
    assert.ok(ts.isIdentifier(input));
    assert.ok(ts.isStringLiteral(needle) || ts.isNoSubstitutionTemplateLiteral(needle));
    anchors.push({ input: input.text, needle: needle.text });
  }
  ts.forEachChild(node, visit);
}
visit(syntax);
test('all copied-source unique anchors reject missing and duplicated forms', () => {
  const source = inputs();
  const result = instrumentCopiedOverlap(source);
  assert.equal(typeof result.coordinator, 'string');
  assert.ok(anchors.length >= 15);
  for (const { input, needle } of anchors) {
    assert.equal(source[input].split(needle).length, 2, input);
    assert.throws(
      () =>
        instrumentCopiedOverlap({
          ...source,
          [input]: source[input].replace(needle, 'missing-anchor'),
        }),
      input + ' missing',
    );
    assert.throws(
      () => instrumentCopiedOverlap({ ...source, [input]: source[input] + needle }),
      input + ' duplicated',
    );
  }
});
test('missing fixed sibling refuses', async () => {
  const dir = mkdtempSync(join(realpathSync(tmpdir()), 'overlap-source-control-'));
  try {
    const sourcePath = join(dir, 'framework-overlap-proof-source.mjs');
    writeFileSync(sourcePath, moduleText);
    const isolated = await import(pathToFileURL(sourcePath).href);
    assert.throws(() => isolated.instrumentCopiedOverlap(inputs()));
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
test('real qualified transport source composes without a Next process', () => {
  const source = inputs();
  const identity = verifyFrameworkInstallation({ appRoot: join(repo, 'frontend/apps/web') });
  const transport = instrumentCopiedTransport({ source: source.runtime, identity });
  const result = instrumentCopiedOverlap({
    ...source,
    runtime: transport.runtime,
    observer: transport.observer,
  });
  assert.ok(
    result.runtime.includes(
      "import { createCopiedOverlapProof } from './framework-proof-overlap.mjs';",
    ),
  );
  assert.ok(result.observer.includes('overlap.encoderClose()'));
  for (const [name, kind] of [
    ['runtime', ts.ScriptKind.JS],
    ['requestRuntime', ts.ScriptKind.TS],
    ['snapshotPool', ts.ScriptKind.TS],
    ['sessionStore', ts.ScriptKind.TS],
    ['page', ts.ScriptKind.TSX],
    ['observer', ts.ScriptKind.JS],
    ['coordinator', ts.ScriptKind.JS],
  ]) {
    const parsed = ts.createSourceFile(name, result[name], ts.ScriptTarget.Latest, true, kind);
    assert.deepEqual(parsed.parseDiagnostics, [], name + ' syntax');
  }
});
