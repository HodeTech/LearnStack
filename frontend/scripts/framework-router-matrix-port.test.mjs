// Pure controls: no socket, Next build, or copied runtime.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import {
  chmodSync,
  linkSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  renameSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';

import { createCopiedRouterHttpPort } from '../apps/web/scripts/framework-router-matrix-port-host.mjs';
import { routerPipeInventory } from '../apps/web/scripts/framework-router-generated-pipes.mjs';
import { copiedRouterMatrixRouteSource } from '../apps/web/scripts/framework-router-matrix-source.mjs';

const refused = /Copied router HTTP port refused/;
function fixture() {
  const root = realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-router-port-control-')));
  const app = join(root, 'frontend/apps/web');
  mkdirSync(app, { recursive: true });
  const portPath = join(root, '.router-http.port');
  const pipePath = join(root, '.router-pipe.events');
  const family = Object.freeze({
    pid: process.pid,
    group: process.pid,
    parentPid: process.pid + 1,
    leaseNonce: 'a'.repeat(32),
    incarnation: 'b'.repeat(32),
  });
  const lease = Object.freeze({
    pid: process.pid + 1,
    nonce: family.leaseNonce,
  });
  const state = { family, lease, healthy: true, retired: false };
  const broker = {
    assertHealthy() {
      if (!state.healthy) throw new Error('Private fake broker failed');
    },
    capture: {
      family: () => state.family,
      lease: () => state.lease,
      retired: () => state.retired,
    },
    records: () => [{ path: join(app, '.framework-state/writer.json') }],
  };
  const portFrame = (port = 12345) => JSON.stringify({ version: 1, pid: family.pid, port }) + '\n';
  writeFileSync(portPath, portFrame(), { flag: 'wx', mode: 0o600 });
  writeFileSync(pipePath, '', { flag: 'wx', mode: 0o600 });
  // Synthetic codec records only; this fixture NEVER claims installed/build admission.
  const point = Object.freeze({ line: 1, column: 0, endLine: 1, endColumn: 20 });
  const files = Object.freeze(
    Array.from({ length: 28 }, (_, index) =>
      Object.freeze({
        index,
        kind: index < 27 ? 'installed' : 'generated',
        path: index < 27 ? 'dist/server/control-' + index + '.js' : 'next/server/control.js',
        bytes: 42,
        sha256: 'c'.repeat(64),
        map: null,
        entries: Object.freeze([{ responseName: 'response', point }]),
      }),
    ),
  );
  const sites = Object.freeze(
    files.map((file) =>
      Object.freeze({
        index: file.index,
        fileIndex: file.index,
        entryIndex: 0,
        kind: file.kind,
        path: file.path,
        bytes: file.bytes,
        sha256: file.sha256,
        manifestSha256: file.kind === 'generated' ? 'd'.repeat(64) : null,
        responseName: 'response',
        point,
      }),
    ),
  );
  const union = Object.freeze({
    format: 1,
    manifestSha256: 'd'.repeat(64),
    censusSha256: 'e'.repeat(64),
    parser: {},
    referenceSha256: 'c'.repeat(64),
    files,
    sites,
  });
  const compressionSha256 = 'f'.repeat(64);
  writeFileSync(
    join(root, '.router-pipe-inventory.json'),
    JSON.stringify({ version: 1, pid: family.pid, inventory: routerPipeInventory(union) }) + '\n',
    { flag: 'wx', mode: 0o600 },
  );
  for (const name of [
    '.router-filter.events',
    '.router-pipe-trace.events',
    '.router-pipe-attested.events',
  ])
    writeFileSync(join(root, name), '', { flag: 'wx', mode: 0o600 });

  return {
    root,
    app,
    union,
    compressionSha256,
    portPath,
    pipePath,
    family,
    state,
    broker,
    portFrame,
    dispose: () => rmSync(root, { recursive: true, force: true }),
  };
}
function withFixture(run) {
  const owned = fixture();
  try {
    run(owned);
  } finally {
    owned.dispose();
  }
}

test('route bytes are a fixed source-bound literal', () => {
  assert.equal(
    createHash('sha256').update(copiedRouterMatrixRouteSource).digest('hex'),
    '1626bf258547e1a1b78affd55268acbed9727051d27128bb5088526b815e3306',
  );
  assert.match(copiedRouterMatrixRouteSource, /export function GET\(\)/);
  assert.match(copiedRouterMatrixRouteSource, /new ReadableStream<Uint8Array>/);
  assert.match(copiedRouterMatrixRouteSource, /new Uint8Array\(8192\)\.fill\(65\)/);
  assert.equal(copiedRouterMatrixRouteSource.includes('process.env'), false);
});

function completeEvidence(f) {
  const cases = ['healthy-identity', 'healthy-gzip', 'healthy-deflate'];
  const write = (name, rows) =>
    writeFileSync(join(f.root, name), rows.map((row) => JSON.stringify(row) + '\n').join(''));
  write(
    '.router-pipe.events',
    cases.map((name, index) => ({
      version: 1,
      pid: f.family.pid,
      sequence: index + 1,
      case: name,
      index: 27,
    })),
  );
  write('.router-filter.events', [
    {
      version: 1,
      pid: f.family.pid,
      sequence: 1,
      case: cases[0],
      subject: 'compression',
      subjectSha256: f.compressionSha256,
      kind: 'filtered-header',
      headerClass: 'array-one-text-plain',
    },
  ]);
  const trace = [];
  const add = (k, t, h, x) =>
    trace.push({ v: 1, p: f.family.pid, n: trace.length + 1, k, s: 27, t, h, x });
  add('script', -1, 'setup', 'ready');
  add('resolved', 0, 'setup', 'match');
  for (let i = 0; i < 3; i++) {
    add('hit', 0, 'case-' + i, 'known');
    if (i === 0) add('source', -1, 'case-0', 'match');
    add('mark', 0, 'case-' + i, 'true');
    add('entry', 0, 'case-' + i, 'router-seen');
  }
  write('.router-pipe-trace.events', trace);
  write(
    '.router-pipe-attested.events',
    cases.map((name, index) => ({
      version: 1,
      pid: f.family.pid,
      sequence: index + 1,
      case: name,
      index: 27,
      scriptId: '123',
      sha256: 'c'.repeat(64),
      line: 1,
      column: 2,
    })),
  );
}
test('one captured immutable port and three exact synthetic witnessed rows are readable', () =>
  withFixture((f) => {
    const host = createCopiedRouterHttpPort(f);
    assert.equal(host.port, 12345);
    host.assertBinding();
    assert.deepEqual(host.pipeEvents(), []);
    completeEvidence(f);
    assert.deepEqual(
      host.pipeEvents().map((row) => row.index),
      [27, 27, 27],
    );
    host.assertCase('healthy-identity');
    assert.throws(() => host.assertClosed(), /Copied router pipe evidence refused/);
    f.state.retired = true;
    assert.equal(host.assertClosed().attestations.length, 3);
  }));

test('same-inode port rewrite is refused even with the same byte length', () =>
  withFixture((f) => {
    const host = createCopiedRouterHttpPort(f);
    assert.equal(f.portFrame(12346).length, f.portFrame(12345).length);
    writeFileSync(f.portPath, f.portFrame(12346));
    assert.throws(() => host.port, refused);
    assert.throws(() => host.pipeEvents(), refused);
  }));

test('replacement inode with identical bytes is refused', () =>
  withFixture((f) => {
    const host = createCopiedRouterHttpPort(f);
    const same = readFileSync(f.portPath);
    const substitute = join(f.root, '.router-http-substitute');
    writeFileSync(substitute, same, { flag: 'wx', mode: 0o600 });
    renameSync(substitute, f.portPath);
    assert.throws(() => host.assertBinding(), refused);
  }));

test('mode and hard-link changes are refused after capture', () => {
  withFixture((f) => {
    const host = createCopiedRouterHttpPort(f);
    chmodSync(f.portPath, 0o644);
    assert.throws(() => host.assertBinding(), refused);
  });
  withFixture((f) => {
    const host = createCopiedRouterHttpPort(f);
    linkSync(f.portPath, join(f.root, '.router-http-alias'));
    assert.throws(() => host.assertBinding(), refused);
  });
});

test('wrong PID, extra field, and appended frame refuse initial capture', () => {
  for (const frame of [
    JSON.stringify({ version: 1, pid: 1, port: 12345 }) + '\n',
    JSON.stringify({
      version: 1,
      pid: process.pid,
      port: 12345,
      path: '/tmp',
    }) + '\n',
    JSON.stringify({ version: 1, pid: process.pid, port: 12345 }) +
      '\n' +
      JSON.stringify({ version: 1, pid: process.pid, port: 12345 }) +
      '\n',
  ])
    withFixture((f) => {
      writeFileSync(f.portPath, frame);
      assert.throws(() => createCopiedRouterHttpPort(f), refused);
    });
});

test('captured family or lease replacement refuses the existing port', () => {
  withFixture((f) => {
    const host = createCopiedRouterHttpPort(f);
    f.state.family = Object.freeze({
      ...f.family,
      incarnation: 'c'.repeat(32),
    });
    assert.throws(() => host.port, refused);
  });
  withFixture((f) => {
    const host = createCopiedRouterHttpPort(f);
    f.state.lease = Object.freeze({ ...f.state.lease });
    assert.throws(() => host.assertBinding(), refused);
  });
});

test('app alias and duplicate pipe case are refused', () => {
  withFixture((f) => {
    const alias = join(f.root, 'web-alias');
    symlinkSync(f.app, alias, 'dir');
    assert.throws(() => createCopiedRouterHttpPort({ ...f, app: alias }), refused);
  });
  withFixture((f) => {
    const host = createCopiedRouterHttpPort(f);
    writeFileSync(
      f.pipePath,
      [1, 2]
        .map(
          (sequence) =>
            JSON.stringify({
              version: 1,
              pid: f.family.pid,
              sequence,
              case: 'healthy-gzip',
              index: 0,
            }) + '\n',
        )
        .join(''),
    );
    assert.throws(() => host.pipeEvents(), refused);
  });
});

for (const [name, file, mutate] of [
  [
    'foreign loaded hash',
    '.router-pipe-attested.events',
    (rows) => {
      rows[0].sha256 = '0'.repeat(64);
    },
  ],
  [
    'unmarked actual entry',
    '.router-pipe-trace.events',
    (rows) => {
      rows.find((row) => row.k === 'mark').x = 'false';
    },
  ],
  [
    'wrong filtered baseline',
    '.router-filter.events',
    (rows) => {
      rows[0].headerClass = 'string-text-plain';
    },
  ],
  [
    'producer inventory substitution',
    '.router-pipe-inventory.json',
    (rows) => {
      rows[0].inventory.manifestSha256 = '0'.repeat(64);
    },
  ],
])
  test('host evidence refuses ' + name, () =>
    withFixture((f) => {
      const host = createCopiedRouterHttpPort(f);
      completeEvidence(f);
      const path = join(f.root, file);
      const rows = readFileSync(path, 'utf8').trimEnd().split('\n').map(JSON.parse);
      mutate(rows);
      writeFileSync(path, rows.map((row) => JSON.stringify(row) + '\n').join(''));
      assert.throws(() => host.pipeEvents(), /Copied router (?:HTTP port|pipe evidence) refused/);
    }),
  );

test('previously read trace cannot be rewritten or replaced with identical records', () => {
  for (const replacement of [false, true])
    withFixture((f) => {
      const host = createCopiedRouterHttpPort(f);
      completeEvidence(f);
      host.pipeEvents();
      const path = join(f.root, '.router-pipe-trace.events');
      const bytes = readFileSync(path);
      if (replacement) {
        const next = path + '.replacement';
        writeFileSync(next, bytes, { mode: 0o600 });
        renameSync(next, path);
      } else writeFileSync(path, bytes.toString().replace('"ready"', '"early"'));
      assert.throws(() => host.pipeEvents(), /Copied router (?:HTTP port|pipe evidence) refused/);
    });
});
