// Pure copied-source controls. No fixture build, listener, or Next process starts here.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

import { verifyFrameworkInstallation } from './framework-installation.mjs';
import { instrumentCopiedBffRuntime } from '../apps/web/scripts/bff-proof-host.mjs';
import {
  copiedRouterMatrixRouteSource,
  instrumentCopiedRouterMatrix,
} from '../apps/web/scripts/framework-router-matrix-source.mjs';
import { instrumentCopiedTransport } from '../apps/web/scripts/framework-transport-proof-source.mjs';

const frontendRoot = realpathSync(fileURLToPath(new URL('../', import.meta.url)));
const appRoot = join(frontendRoot, 'apps/web');
const ts = createRequire(join(appRoot, 'package.json'))('typescript');
const identity = verifyFrameworkInstallation({ appRoot });
const source = readFileSync(join(appRoot, 'scripts/public-server-runtime.mjs'), 'utf8');
const issuer = 'https://identity.example.test/realms/learnstack';
const bff = instrumentCopiedBffRuntime(source, {
  mutation: undefined,
  external: false,
  uiMode: true,
  isolatedProtocol: true,
  disabledOwnPreference: false,
  fakeIssuer: issuer,
  fakeLogout: issuer + '/protocol/openid-connect/logout',
  sites: [
    { host: 'institution.example.test:3000', displayName: 'Institution', locale: 'en' },
    { host: 'other.example.test:3000', displayName: 'Diğer kurum', locale: 'tr' },
  ],
  marker: 'a'.repeat(43),
  clockBase: 1800000000000,
});
const transport = instrumentCopiedTransport({ source: bff, identity });
const error = /Exact copied router-matrix anchor/;

test('current BFF and transport copies generate one source-bound router matrix', () => {
  const matrix = instrumentCopiedRouterMatrix({
    runtime: transport.runtime,
    observer: transport.observer,
    identity,
  });
  assert.equal(matrix.pipes.length, 27);
  assert.match(matrix.observer, /deriveRouterPipeUnion\(framework, SPEC\.pipes, SPEC\.parser\)/);
  assert.match(matrix.observer, /attestPipeFrame\(point, frame\)/);
  assert.match(matrix.observer, /source-mismatch/);
  assert.match(matrix.observer, /point\.responseName/);
  assert.match(matrix.observer, /o\.getHeader\('Content-Type'\)/);
  assert.match(matrix.observer, /\['healthy-gzip','healthy-deflate'\]\.includes\(active\.name\)/);
  assert.match(matrix.observer, /pipeSequence !== 3/);
  const spec = JSON.parse(/^const SPEC = (\{[^\n]*\});$/m.exec(matrix.observer)[1]);
  assert.equal(spec.parser.version, '5.6.3');
  assert.match(spec.parser.entrySha256, /^[a-f0-9]{64}$/);

  assert.equal(new Set(matrix.pipes.map((row) => row.path)).size, 27);
  assert.equal(
    createHash('sha256').update(copiedRouterMatrixRouteSource).digest('hex'),
    '1626bf258547e1a1b78affd55268acbed9727051d27128bb5088526b815e3306',
  );
  for (const [name, sourceText, kind] of [
    ['runtime.mjs', matrix.runtime, ts.ScriptKind.JS],
    ['observer.mjs', matrix.observer, ts.ScriptKind.JS],
    ['route.ts', copiedRouterMatrixRouteSource, ts.ScriptKind.TS],
  ]) {
    const parsed = ts.createSourceFile(name, sourceText, ts.ScriptTarget.Latest, true, kind);
    assert.equal(parsed.parseDiagnostics.length, 0, 'Generated ' + name + ' parses');
  }
  const root = realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-router-source-')));
  try {
    for (const [name, bytes] of [
      ['runtime.mjs', matrix.runtime],
      ['observer.mjs', matrix.observer],
    ]) {
      const path = join(root, name);
      writeFileSync(path, bytes, { flag: 'wx', mode: 0o600 });
      execFileSync(process.execPath, ['--check', path], {
        timeout: 5000,
        env: { PATH: process.env.PATH, NODE_ENV: 'production' },
        stdio: 'pipe',
      });
    }
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('missing copied runtime anchor refuses before source generation', () => {
  assert.throws(
    () =>
      instrumentCopiedRouterMatrix({
        runtime: transport.runtime.replace('  let server;', '  let missingServer;'),
        observer: transport.observer,
        identity,
      }),
    error,
  );
});

test('duplicated copied runtime anchor refuses before source generation', () => {
  assert.throws(
    () =>
      instrumentCopiedRouterMatrix({
        runtime: transport.runtime + '\n  let server;',
        observer: transport.observer,
        identity,
      }),
    error,
  );
});

test('missing filtered selection anchor refuses the private transform', () => {
  assert.throws(
    () =>
      instrumentCopiedRouterMatrix({
        runtime: transport.runtime,
        observer: transport.observer.replace(
          "        active.noCompression = true;\n        event('none', reason);",
          '        active.noCompression = false;',
        ),
        identity,
      }),
    /Exact copied router-matrix anchor/,
  );
});

test('missing actual handler seam refuses preseed generation', () => {
  assert.throws(
    () =>
      instrumentCopiedRouterMatrix({
        runtime: transport.runtime,
        observer: transport.observer.replace(
          '        const selected = active?.response === response;\n        let result;',
          '        const selected = false;',
        ),
        identity,
      }),
    /Exact copied router-matrix anchor/,
  );
});

test('closed filtered-header record fits the fixed private ledger bound', () => {
  const line =
    JSON.stringify({
      version: 1,
      pid: 99999999,
      sequence: 2,
      case: 'healthy-deflate',
      subject: 'compression',
      subjectSha256: 'f'.repeat(64),
      kind: 'filtered-header',
      headerClass: 'array-one-text-plain',
    }) + '\n';
  assert.ok(Buffer.byteLength(line) <= 256);
  assert.ok(Buffer.byteLength(line) * 3 <= 768);
});
